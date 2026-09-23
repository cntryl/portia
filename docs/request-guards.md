# Request guards

Portia's unary request path is:

```text
authorization → pipeline behaviors → guards → handler
```

Authorization answers whether an actor may attempt an operation. A guard performs an asynchronous
preflight against surrounding application state immediately before the handler. The handler and
aggregate remain authoritative because anything a guard reads can become stale.

Pure formatting, normalization, reserved-value checks, and other synchronous domain policies stay
in application value objects and aggregates. Portia adds no guard DSL or domain-policy API.

## One request, authorizer, guard, and handler

This feature keeps actor policy, an asynchronous maintenance-window check, and the authoritative
aggregate decision in their separate owners:

```csharp
using System.Security.Claims;
using Cntryl.Portia;

public sealed record OpenAccount(Uuid AccountId, int InitialBalance) : IRequest;

public interface IAccountAccess
{
    ValueTask<bool> CanOpenAsync(ClaimsPrincipal actor, CancellationToken ct);
}

public sealed class OpenAccountAuthorizer(IAccountAccess access)
    : IRequestAuthorizer<OpenAccount>
{
    public async ValueTask<Result> AuthorizeAsync(
        IRequestContext<OpenAccount> context,
        CancellationToken ct) =>
        await access.CanOpenAsync(context.Actor, ct)
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden,
                "The actor may not open accounts."));
}

public interface IAccountOperations
{
    ValueTask<bool> AreOpenAsync(CancellationToken ct);
}

public sealed class AccountOperationsGuard(IAccountOperations operations)
    : IRequestGuard<OpenAccount>
{
    public async ValueTask<Result> GuardAsync(
        IRequestContext<OpenAccount> context,
        CancellationToken ct) =>
        await operations.AreOpenAsync(ct)
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Conflict,
                "Account operations are temporarily closed.", isTransient: true));
}

[Discriminator("banking.account-opened")]
public sealed record AccountOpened(int InitialBalance) : DomainEvent;

public sealed class Account : Aggregate
{
    public Account(Uuid id)
        : base(id, new EventStreamAddress("banking", "accounts", id.ToString()))
    {
        On<AccountOpened>(_ => IsOpen = true);
    }

    public bool IsOpen { get; private set; }

    public Result Open(int initialBalance)
    {
        if (IsOpen)
            return Result.Failure(new RequestError(RequestErrorKind.Conflict,
                "The account is already open."));
        if (initialBalance < 0)
            return Result.Failure(new RequestError(RequestErrorKind.Validation,
                "The initial balance cannot be negative."));

        RaiseEvent(new AccountOpened(initialBalance));
        return Result.Success;
    }
}

public sealed class OpenAccountHandler(IAggregateReader reader, IAggregateWriter writer)
    : IRequestHandler<OpenAccount>
{
    public async ValueTask<Result> HandleAsync(
        IRequestContext<OpenAccount> context,
        CancellationToken ct)
    {
        var account = await reader.HydrateAsync(
            new Account(context.Request.AccountId), ct);
        var result = account.Open(context.Request.InitialBalance);
        if (!result.IsSuccess)
            return result;

        await writer.SaveAsync(account, context, ct);
        return result;
    }
}

public static class AccountFeature
{
    public static PortiaBuilder AddAccounts(this PortiaBuilder builder) => builder
        .AddRequestHandler<OpenAccountHandler>()
        .AddRequestAuthorizer<OpenAccountAuthorizer>()
        .AddRequestGuard<AccountOperationsGuard>();
}
```

The authorizer runs first and may deny without entering any behavior or guard. The guard may return
an ordinary transient failure without invoking the handler. Even after both succeed, `Account.Open`
still owns the domain invariant and the repository save still owns optimistic concurrency.

## One guard reused across many requests

Request-family interfaces are application-owned capabilities. A single guard can target the
capability while unrelated request fields and result shapes remain concrete:

```csharp
using Cntryl.Portia;

public interface IClaimsTenantSlug : IRequestBase
{
    string TenantSlug { get; }
}

public sealed record CreateTenant(string TenantSlug) : IRequest, IClaimsTenantSlug;
public sealed record RenameTenant(Uuid TenantId, string TenantSlug) : IRequest, IClaimsTenantSlug;
public sealed record PreviewTenantUrl(string TenantSlug) : IRequest<string>, IClaimsTenantSlug;

public interface ITenantSlugDirectory
{
    ValueTask<bool> IsAvailableAsync(string slug, CancellationToken ct);
}

public sealed class AvailableTenantSlugGuard(ITenantSlugDirectory directory)
    : IRequestGuard<IClaimsTenantSlug>
{
    public async ValueTask<Result> GuardAsync(
        IRequestContext<IClaimsTenantSlug> context,
        CancellationToken ct) =>
        await directory.IsAvailableAsync(context.Request.TenantSlug, ct)
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Conflict,
                "That tenant slug appears to be occupied."));
}

public static class TenantSlugFeature
{
    public static PortiaBuilder AddTenantSlugGuard(this PortiaBuilder builder) =>
        builder.AddRequestGuard<AvailableTenantSlugGuard>();
}
```

Portia matches `IClaimsTenantSlug` by assignability, so the guard serves all three requests. New
requests opt in by implementing the interface; the guard does not enumerate concrete types.

## Soft rolling windows: "have you failed too many sign-ins?"

Rate limits, cooldowns, and lockout windows are the case guards fit best. They are time-bounded
questions answered from history, not invariants an aggregate can enforce, and a slightly stale
answer only ever lets one extra attempt through.

The aggregate records what happened. A failed sign-in changes no state, so it audits:

```csharp
using Cntryl.Portia;

[Discriminator("identity.sign-in.failed")]
public sealed record SignInFailed(string Reason) : DomainEvent;

public sealed record AuthenticatedIdentity(Uuid UserId);

public sealed class UserIdentity : Aggregate
{
    public UserIdentity(Uuid id)
        : base(id, new EventStreamAddress("identity", "users", id.ToString()))
    {
    }

    public Result<AuthenticatedIdentity> Authenticate(string secret)
    {
        if (Verify(secret))
            return Result<AuthenticatedIdentity>.Success(new AuthenticatedIdentity(Id));
        AuditEvent(new SignInFailed("bad-secret"));
        return Result<AuthenticatedIdentity>.Failure(
            new RequestError(RequestErrorKind.Unauthorized, "Those credentials did not match."));
    }

    bool Verify(string secret) => secret.Length > 0;
}
```

The handler commits either way, so the rejected attempt keeps its audit:

```csharp
public sealed record SignIn(Uuid UserId, string Secret) : IRequest<AuthenticatedIdentity>, ICallable;

public sealed class SignInHandler(IAggregateExecutor aggregates)
    : IRequestHandler<SignIn, AuthenticatedIdentity>
{
    public ValueTask<Result<AuthenticatedIdentity>> HandleAsync(
        IRequestContext<SignIn> context,
        CancellationToken ct) =>
        aggregates.ExecuteAsync(
            new UserIdentity(context.Request.UserId),
            identity => AggregateOutcome.Commit(identity.Authenticate(context.Request.Secret)),
            context,
            ct);
}
```

A projection turns those audits into the window, and the guard reads it:

```csharp
public interface ISignInAttempts
{
    ValueTask<int> FailuresSinceAsync(Uuid userId, DateTimeOffset since, CancellationToken ct);
}

public sealed class SignInWindowGuard(ISignInAttempts attempts, TimeProvider time)
    : IRequestGuard<SignIn>
{
    public async ValueTask<Result> GuardAsync(
        IRequestContext<SignIn> context,
        CancellationToken ct) =>
        await attempts.FailuresSinceAsync(
            context.Request.UserId, time.GetUtcNow().AddMinutes(-15), ct) < 5
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden,
                "Too many recent sign-in attempts. Try again later."));
}
```

The guard runs before the handler, so a locked-out caller never reaches credential verification.
The count is eventually consistent, and that is the right trade here: the window is a soft policy,
not a hard invariant, so the aggregate stays free of attempt counters and the check costs one read.

Model it as aggregate state only when it becomes a real invariant — an account disabled until an
administrator re-enables it. That changes state, so the operation raises an event instead of
auditing, and the aggregate rejects the attempt authoritatively.

## String permissions resolved outside the JWT

The principal establishes actor identity. It need not carry the application's current permission
set. An authorizer can ask an application authorization store using actor, tenant, and a stable
string permission:

```csharp
using System.Security.Claims;
using Cntryl.Portia;

public sealed record ReadTenantBilling(Uuid TenantId) : IRequest<BillingView>;
public sealed record BillingView(string Plan);

public interface IApplicationAuthorizationStore
{
    ValueTask<bool> AllowsAsync(
        ClaimsPrincipal actor,
        Uuid tenantId,
        string permission,
        CancellationToken ct);
}

public sealed class ReadTenantBillingAuthorizer(IApplicationAuthorizationStore authorization)
    : IRequestAuthorizer<ReadTenantBilling>
{
    public async ValueTask<Result> AuthorizeAsync(
        IRequestContext<ReadTenantBilling> context,
        CancellationToken ct) =>
        await authorization.AllowsAsync(context.Actor, context.Request.TenantId,
            "tenant.billing.read", ct)
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden,
                "Billing access was denied."));
}

public static class BillingAuthorization
{
    public static PortiaBuilder AddBillingAuthorization(this PortiaBuilder builder) =>
        builder.AddRequestAuthorizer<ReadTenantBillingAuthorizer>();
}
```

Authentication still validates the credential at the ingress boundary. This authorizer uses the
resulting principal only as identity and obtains the permission decision from current application
state.

## One permission authorizer for many request types

The permission itself can be part of an application-owned request-family contract. Declare it as
an explicit computed property: it is available through the interface inside the authorizer but is
not a public concrete-request member for input binding or serialization.

```csharp
using System.Security.Claims;
using Cntryl.Portia;

public interface ITenantPermissionRequest : IRequestBase
{
    Uuid TenantId { get; }
    string RequiredPermission { get; }
}

public sealed record ReadBilling(Uuid TenantId)
    : IRequest<BillingSummary>, ITenantPermissionRequest
{
    string ITenantPermissionRequest.RequiredPermission => "tenant.billing.read";
}

public sealed record ChangeBillingPlan(Uuid TenantId, string Plan)
    : IRequest, ITenantPermissionRequest
{
    string ITenantPermissionRequest.RequiredPermission => "tenant.billing.write";
}

public sealed record ExportTenantMembers(Uuid TenantId)
    : IRequest<byte[]>, ITenantPermissionRequest
{
    string ITenantPermissionRequest.RequiredPermission => "tenant.members.export";
}

public sealed record BillingSummary(string Plan);

public interface ITenantAuthorizationStore
{
    ValueTask<bool> AllowsAsync(
        ClaimsPrincipal actor,
        Uuid tenantId,
        string permission,
        CancellationToken ct);
}

public sealed class TenantPermissionAuthorizer(ITenantAuthorizationStore authorization)
    : IRequestAuthorizer<ITenantPermissionRequest>
{
    public async ValueTask<Result> AuthorizeAsync(
        IRequestContext<ITenantPermissionRequest> context,
        CancellationToken ct) =>
        await authorization.AllowsAsync(context.Actor, context.Request.TenantId,
            context.Request.RequiredPermission, ct)
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden,
                "The required tenant permission was denied."));
}

public static class TenantPermissionFeature
{
    public static PortiaBuilder AddTenantPermissions(this PortiaBuilder builder) =>
        builder.AddRequestAuthorizer<TenantPermissionAuthorizer>();
}
```

`RequiredPermission` cannot be supplied by a caller because it is not a public property of any
concrete record. Each request's code fixes its permission, while one authorizer owns lookup and
denial behavior.

## Guard versus authoritative invariant

A read model can reject obviously occupied slugs cheaply, but a successful read is only a hint. In
this example every slug has one authoritative aggregate stream; the aggregate verifies ownership,
and the repository save rejects an optimistic-concurrency race:

```csharp
using Cntryl.Portia;

public sealed record ClaimTenantSlug(Uuid SlugId, Uuid TenantId, string Slug) : IRequest;

public interface ITenantSlugReadModel
{
    ValueTask<bool> AppearsOccupiedAsync(string slug, CancellationToken ct);
}

public sealed class TenantSlugReadGuard(ITenantSlugReadModel readModel)
    : IRequestGuard<ClaimTenantSlug>
{
    public async ValueTask<Result> GuardAsync(
        IRequestContext<ClaimTenantSlug> context,
        CancellationToken ct) =>
        await readModel.AppearsOccupiedAsync(context.Request.Slug, ct)
            ? Result.Failure(new RequestError(RequestErrorKind.Conflict,
                "That slug appears to be occupied."))
            : Result.Success;
}

[Discriminator("tenants.slug-claimed")]
public sealed record TenantSlugClaimed(Uuid TenantId, string Slug) : DomainEvent;

public sealed class TenantSlug : Aggregate
{
    public TenantSlug(Uuid slugId)
        : base(slugId, new EventStreamAddress("tenants", "slugs", slugId.ToString()))
    {
        On<TenantSlugClaimed>(ev => Owner = ev.TenantId);
    }

    public Uuid? Owner { get; private set; }

    public Result Claim(Uuid tenantId, string slug)
    {
        if (Owner is { } owner && owner != tenantId)
            return Result.Failure(new RequestError(RequestErrorKind.Conflict,
                "That slug is owned by another tenant."));
        if (Owner == tenantId)
            return Result.Success;

        RaiseEvent(new TenantSlugClaimed(tenantId, slug));
        return Result.Success;
    }
}

public sealed class ClaimTenantSlugHandler(IAggregateReader reader, IAggregateWriter writer)
    : IRequestHandler<ClaimTenantSlug>
{
    public async ValueTask<Result> HandleAsync(
        IRequestContext<ClaimTenantSlug> context,
        CancellationToken ct)
    {
        var request = context.Request;
        var ownership = await reader.HydrateAsync(new TenantSlug(request.SlugId), ct);
        var result = ownership.Claim(request.TenantId, request.Slug);
        if (!result.IsSuccess)
            return result;

        await writer.SaveAsync(ownership, context, ct);
        return result;
    }
}
```

Another request may claim the slug after `TenantSlugReadGuard` succeeds. That does not weaken the
decision: the later handler observes existing ownership, or its save loses the concurrency race and
throws `EventStreamConcurrencyException`. A guard is never a replacement for either check.

## Registration and execution

A complete composition keeps the stages visible:

```csharp
using Cntryl.Portia;
using Microsoft.Extensions.DependencyInjection;

public interface IApplicationCommand : IRequestBase;
public sealed record RunApplicationCommand : IRequest, IApplicationCommand;

public sealed class RunApplicationCommandHandler : IRequestHandler<RunApplicationCommand>
{
    public ValueTask<Result> HandleAsync(
        IRequestContext<RunApplicationCommand> context,
        CancellationToken ct) => ValueTask.FromResult(Result.Success);
}

public sealed class ApplicationAuthorizer : IRequestAuthorizer<IApplicationCommand>
{
    public ValueTask<Result> AuthorizeAsync(
        IRequestContext<IApplicationCommand> context,
        CancellationToken ct) => ValueTask.FromResult(Result.Success);
}

public sealed class ApplicationGuard : IRequestGuard<IApplicationCommand>
{
    public ValueTask<Result> GuardAsync(
        IRequestContext<IApplicationCommand> context,
        CancellationToken ct) => ValueTask.FromResult(Result.Success);
}

public static class ApplicationSetup
{
    public static PortiaBuilder AddApplication(this IServiceCollection services) =>
        services.AddPortia()
            .AddRequestHandler<RunApplicationCommandHandler>()
            .AddRequestAuthorizer<ApplicationAuthorizer>(AuthorizationStage.ResourceAccess)
            .AddRequestPipelineBehavior<ApplicationTransactionBehavior>(order: 0)
            .AddRequestGuard<ApplicationGuard>();
}

public sealed class ApplicationTransactionBehavior
    : IRequestPipelineBehavior<RunApplicationCommand>
{
    public async ValueTask<Result> HandleAsync(
        IRequestContext<RunApplicationCommand> context,
        RequestPipelineNext continuation,
        CancellationToken ct)
    {
        // Begin an application transaction here.
        var result = await continuation(ct);
        // Commit or roll back here.
        return result;
    }
}
```

`AddRequestGuard<TGuard>()` is source-generated and reflection-free. The guard is registered as a
scoped service. For every command, result-bearing query, and streamed request, Portia selects every
guard whose scope is assignable from that request and runs them sequentially in registration order.
The first failure wins. A command receives that same `Result`; a query receives `Result<T>.Failure`
carrying the same `RequestError`; a stream ends before its first item with `RequestGuardException`
carrying that error, which HTTP streaming maps to a problem response. If every guard succeeds, Portia
returns the handler's result unchanged.

Cancellation and exceptions propagate. Returning `default(Result)` is a framework-boundary error
that names the offending guard. `AuthorizeAsync` does not run guards. An outer behavior that returns
without invoking its continuation also skips the guards, because neither the innermost preflight nor
the handler executes. One type cannot be registered as both a request authorizer and a request
guard; composition rejects it, because authorization and preflight run at different phases.

Each guard records `portia.guard.duration` with its component name and outcome.

Guards and authorizers read; they never change state. Take `IAggregateReader` to hydrate an
aggregate and `IDomainEventReader` to read events. Portia's practice analyzers report `PORTIA105`
for a guard, and `PORTIA106` for an authorizer, that takes one of Portia's write or dispatch APIs:
`IRequestBus`, a scheduler or queue publisher, `IAggregateWriter`, `IAggregateExecutor`, or an event or
projection store. Reading through an HTTP policy service or a read-model database is not reported.

Every direct dispatch is a fresh attempt. Queue retry or redelivery creates a fresh dependency-
injection delivery scope, resolves scoped guards again, and reruns them; guard outcomes are never
cached across attempts.

## Testing the lifecycle

`RequestScenario` in `Cntryl.Portia.Testing` runs a request through the application's real
composition — authorization, pipeline behaviors, guards, and the handler — in a fresh scope, and
asserts on what actually happened:

```csharp
await RequestScenario.For(provider)
    .GivenActor(member)
    .When(new CreateTenant("acme"))
    .ExpectAuthorized()
    .ExpectGuardPassed<AvailableTenantSlugGuard>()
    .ExpectHandled()
    .ExpectSuccess();

await RequestScenario.For(provider)
    .GivenActor(member)
    .When(new CreateTenant("taken"))
    .ExpectGuardFailed<AvailableTenantSlugGuard>(RequestErrorKind.Conflict)
    .ExpectNotHandled();
```

Scenarios and expectations are immutable; awaiting runs the request once and reports every unmet
expectation together with the observed lifecycle. `ExpectDenied` distinguishes an authorization
denial from a guard that fails with `Forbidden`. Result-bearing requests add `ExpectSuccess(value)`,
and streamed requests add `ExpectItems(...)`.

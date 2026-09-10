using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
/// Verifies permission and authorizer ordering across all dispatch shapes using the public
/// explicit registration and an owned dependency-injection scope.
/// </summary>
public sealed class RequestAuthorizationTests
{
    /// <summary>
    /// Verifies that a request with no <see cref="RequiresPermissionAttribute" /> and no
    /// registered authorizer dispatches normally — authorization is opt-in, not a tax on every
    /// request.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchUnguardedRequestWithoutConsultingAnyEvaluator()
    {
        using var busHost = TestRequestBus.Create();
        var bus = busHost.Bus;

        var result = await bus.SendAsync(new GetValue(), RequestActor.System);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// Verifies that a no-result request's declared permission is checked before its handler
    /// runs, and a denial short-circuits dispatch entirely.
    /// </summary>
    [Fact]
    public async Task ShouldBlockNoResultRequestWhenPermissionDenied()
    {
        var handler = new GuardedActionHandler();
        using var busHost = TestRequestBus.Create(guardedActionHandler: handler, permissionEvaluator: TestPermissionEvaluator.DenyAll());
        var bus = busHost.Bus;

        var result = await bus.SendAsync(new GuardedAction(), RequestActor.Anonymous);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, result.Error.Kind);
        Assert.False(handler.WasInvoked);
    }

    /// <summary>
    /// Verifies that a no-result request's declared permission being granted lets dispatch
    /// through to its handler.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchNoResultRequestWhenPermissionGranted()
    {
        var handler = new GuardedActionHandler();
        using var busHost = TestRequestBus.Create(guardedActionHandler: handler, permissionEvaluator: TestPermissionEvaluator.AllowAll());
        var bus = busHost.Bus;

        var result = await bus.SendAsync(new GuardedAction(), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.True(handler.WasInvoked);
    }

    /// <summary>
    /// Verifies that a with-result request's declared permission is checked before its handler
    /// runs, and a denial produces a failed <see cref="Result{T}" /> rather than throwing.
    /// </summary>
    [Fact]
    public async Task ShouldBlockWithResultRequestWhenPermissionDenied()
    {
        using var busHost = TestRequestBus.Create(permissionEvaluator: TestPermissionEvaluator.DenyAll());
        var bus = busHost.Bus;

        var result = await bus.SendAsync(new GuardedQuery(), RequestActor.Anonymous);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, result.Error.Kind);
    }

    /// <summary>
    /// Verifies a <c>{Token}</c> in a RequiresPermission string is substituted with the actual
    /// value off the concrete request instance being dispatched, not a placeholder or the raw
    /// token text — so two requests of the same type with different data are checked against two
    /// different permission strings.
    /// </summary>
    [Fact]
    public async Task ShouldInterpolateRequestPropertyIntoPermissionString()
    {
        var evaluator = TestPermissionEvaluator.AllowAll();
        using var busHost = TestRequestBus.Create(permissionEvaluator: evaluator);
        var bus = busHost.Bus;

        _ = await bus.SendAsync(new GetOrder(42), RequestActor.System);
        _ = await bus.SendAsync(new GetOrder(99), RequestActor.System);

        Assert.Equal(["orders:42:read", "orders:99:read"], evaluator.EvaluatedPermissions);
    }

    /// <summary>
    /// Verifies that a registered <see cref="IRequestAuthorizer{TRequest}" /> runs before the
    /// handler and can deny a request that has no <see cref="RequiresPermissionAttribute" /> at
    /// all — the two mechanisms are independent.
    /// </summary>
    [Fact]
    public async Task ShouldBlockRequestWhenAuthorizerDenies()
    {
        var handler = new AuthorizedActionHandler();
        using var busHost = TestRequestBus.Create(authorizedActionHandler: handler, authorizedActionAuthorizer: new AuthorizedActionAuthorizer());
        var bus = busHost.Bus;

        var result = await bus.SendAsync(new AuthorizedAction(OwnerId: 8), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.False(handler.WasInvoked);
    }

    /// <summary>
    /// Verifies that a registered <see cref="IRequestAuthorizer{TRequest}" /> receives the actual
    /// request instance (row-level data), not just the request type — granting for one instance
    /// and denying for another of the same type.
    /// </summary>
    [Fact]
    public async Task ShouldGrantOrDenyBasedOnRequestInstanceData()
    {
        using var busHost = TestRequestBus.Create(authorizedActionAuthorizer: new AuthorizedActionAuthorizer());
        var bus = busHost.Bus;

        var ownedResult = await bus.SendAsync(new AuthorizedAction(OwnerId: 7), RequestActor.System);
        var deniedResult = await bus.SendAsync(new AuthorizedAction(OwnerId: 8), RequestActor.System);

        Assert.True(ownedResult.IsSuccess);
        Assert.False(deniedResult.IsSuccess);
    }

    /// <summary>
    /// Verifies the documented ordering directly: when a request declares both
    /// <see cref="RequiresPermissionAttribute" /> and an <see cref="IRequestAuthorizer{TRequest}" />,
    /// the permission is evaluated first, and a denial there short-circuits — the authorizer never
    /// runs at all, not just "runs but its result is discarded."
    /// </summary>
    [Fact]
    public async Task ShouldCheckPermissionBeforeAuthorizerAndShortCircuitOnDenial()
    {
        var authorizer = new RecordingGuardedAndAuthorizedActionAuthorizer();
        using var busHost = TestRequestBus.Create(
            guardedAndAuthorizedActionAuthorizer: authorizer,
            permissionEvaluator: TestPermissionEvaluator.DenyAll());
        var bus = busHost.Bus;

        var result = await bus.SendAsync(new GuardedAndAuthorizedAction(), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.False(authorizer.WasInvoked);
    }

    /// <summary>
    /// Verifies that when the permission is granted, the authorizer still runs afterward and can
    /// independently deny the request.
    /// </summary>
    [Fact]
    public async Task ShouldRunAuthorizerAfterPermissionIsGranted()
    {
        var authorizer = new RecordingGuardedAndAuthorizedActionAuthorizer(grant: false);
        using var busHost = TestRequestBus.Create(
            guardedAndAuthorizedActionAuthorizer: authorizer,
            permissionEvaluator: TestPermissionEvaluator.AllowAll());
        var bus = busHost.Bus;

        var result = await bus.SendAsync(new GuardedAndAuthorizedAction(), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.True(authorizer.WasInvoked);
    }

    /// <summary>
    /// Verifies that a streamed request's authorization failure ends the stream with
    /// <see cref="RequestAuthorizationException" /> before the first item is ever produced —
    /// there's no <see cref="Result" /> to wrap for a stream, so this is the failure channel.
    /// </summary>
    [Fact]
    public async Task ShouldThrowForStreamedRequestWhenPermissionDenied()
    {
        var handler = new GuardedSequenceHandler();
        using var busHost = TestRequestBus.Create(guardedSequenceHandler: handler, permissionEvaluator: TestPermissionEvaluator.DenyAll());
        var bus = busHost.Bus;

        var exception = await Assert.ThrowsAsync<RequestAuthorizationException>(async () =>
        {
            await foreach (var _ in bus.StreamAsync(new GuardedSequence(), RequestActor.Anonymous))
            {
            }
        });

        Assert.Equal(RequestErrorKind.Forbidden, exception.Error.Kind);
        Assert.False(handler.WasInvoked);
    }

    /// <summary>
    /// Verifies that a streamed request whose authorization is granted actually produces its
    /// items.
    /// </summary>
    [Fact]
    public async Task ShouldStreamItemsWhenPermissionGranted()
    {
        using var busHost = TestRequestBus.Create(permissionEvaluator: TestPermissionEvaluator.AllowAll());
        var bus = busHost.Bus;

        var items = new List<int>();

        await foreach (var item in bus.StreamAsync(new GuardedSequence(), RequestActor.System))
            items.Add(item);

        Assert.Equal([1, 2, 3], items);
    }

    /// <summary>Every matching request-family policy runs by semantic stage before the handler.</summary>
    [Fact]
    public async Task ShouldComposeTieredRequestFamilyAuthorizers()
    {
        var calls = new List<string>();
        var services = new ServiceCollection();
        _ = services.AddSingleton(calls);
        _ = services.AddSingleton<TieredHandler>();
        _ = services.AddSingleton<PrincipalAuthorizer>();
        _ = services.AddSingleton<AccountRoleAuthorizer>();
        _ = services.AddSingleton<MfaConfirmationAuthorizer>();
        _ = services.AddSingleton<IPermissionEvaluator>(new RecordingPermissionEvaluator(calls));
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<TieredRequest, TieredHandler>(static _ => "accounts:close"));
        _ = services.AddSingleton<RequestAuthorizerRegistration>(
            new RequestAuthorizerRegistration<IRequestBase, PrincipalAuthorizer>(AuthorizationStage.Principal));
        _ = services.AddSingleton<RequestAuthorizerRegistration>(
            new RequestAuthorizerRegistration<IAccountRequest, AccountRoleAuthorizer>(AuthorizationStage.ResourceAccess));
        _ = services.AddSingleton<RequestAuthorizerRegistration>(
            new RequestAuthorizerRegistration<IMfaConfirmedRequest, MfaConfirmationAuthorizer>(AuthorizationStage.StepUp));
        _ = services.AddScoped<IRequestBus, RequestBus>();
        _ = services.AddSingleton<RequestRegistry>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IRequestBus>()
            .SendAsync(new TieredRequest(Uuid.CreateVersion4()), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(["principal", "permission", "account-role", "mfa", "handler"], calls);
    }

}

interface IAccountRequest : IRequestBase
{
    Uuid AccountId { get; }
}

interface IMfaConfirmedRequest : IRequestBase;

sealed record TieredRequest(Uuid AccountId) : IRequest, IAccountRequest, IMfaConfirmedRequest;

sealed class TieredHandler(List<string> calls) : IRequestHandler<TieredRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<TieredRequest> context, CancellationToken ct)
    {
        calls.Add("handler");
        return ValueTask.FromResult(Result.Success);
    }
}

sealed class PrincipalAuthorizer(List<string> calls) : IRequestAuthorizer<IRequestBase>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<IRequestBase> context, CancellationToken ct)
    {
        calls.Add("principal");
        return ValueTask.FromResult(Result.Success);
    }
}

sealed class AccountRoleAuthorizer(List<string> calls) : IRequestAuthorizer<IAccountRequest>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<IAccountRequest> context, CancellationToken ct)
    {
        calls.Add("account-role");
        return ValueTask.FromResult(Result.Success);
    }
}

sealed class MfaConfirmationAuthorizer(List<string> calls) : IRequestAuthorizer<IMfaConfirmedRequest>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<IMfaConfirmedRequest> context, CancellationToken ct)
    {
        calls.Add("mfa");
        return ValueTask.FromResult(Result.Success);
    }
}

sealed class RecordingPermissionEvaluator(List<string> calls) : IPermissionEvaluator
{
    public ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default)
    {
        calls.Add("permission");
        return ValueTask.FromResult(Result.Success);
    }
}

[RequiresPermission("guarded:action")]
sealed record GuardedAction : IRequest;

sealed class GuardedActionHandler : IRequestHandler<GuardedAction>
{
    public bool WasInvoked { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<GuardedAction> context, CancellationToken ct)
    {
        WasInvoked = true;
        return ValueTask.FromResult(Result.Success);
    }
}

[RequiresPermission("guarded:query")]
sealed record GuardedQuery : IRequest<int>;

sealed class GuardedQueryHandler : IRequestHandler<GuardedQuery, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<GuardedQuery> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(1));
}

[RequiresPermission("orders:{OrderId}:read")]
sealed record GetOrder(int OrderId) : IRequest<int>;

sealed class GetOrderHandler : IRequestHandler<GetOrder, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<GetOrder> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(context.Request.OrderId));
}

sealed record AuthorizedAction(int OwnerId) : IRequest;

sealed class AuthorizedActionHandler : IRequestHandler<AuthorizedAction>
{
    public bool WasInvoked { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<AuthorizedAction> context, CancellationToken ct)
    {
        WasInvoked = true;
        return ValueTask.FromResult(Result.Success);
    }
}

// Owns 7; denies anyone else — exercises both grant and deny from the same authorizer using
// the request's own data, without needing two competing authorizers for the same request type
// (which the generator rejects as ambiguous).
sealed class AuthorizedActionAuthorizer : IRequestAuthorizer<AuthorizedAction>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<AuthorizedAction> context, CancellationToken ct) =>
        ValueTask.FromResult(context.Request.OwnerId == 7
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden, "Not the owner.")));
}

[RequiresPermission("guarded:stream")]
sealed record GuardedSequence : IStreamRequest<int>;

sealed class GuardedSequenceHandler : IStreamRequestHandler<GuardedSequence, int>
{
    public bool WasInvoked { get; private set; }

    public async IAsyncEnumerable<int> HandleAsync(
        IRequestContext<GuardedSequence> context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        WasInvoked = true;
        yield return 1;
        yield return 2;
        yield return 3;
        await Task.CompletedTask;
    }
}

// Declares both authorization mechanisms at once, to prove the documented ordering between them.
[RequiresPermission("guarded_and_authorized:action")]
sealed record GuardedAndAuthorizedAction : IRequest;

sealed class GuardedAndAuthorizedActionHandler : IRequestHandler<GuardedAndAuthorizedAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<GuardedAndAuthorizedAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

sealed class RecordingGuardedAndAuthorizedActionAuthorizer(bool grant = true) : IRequestAuthorizer<GuardedAndAuthorizedAction>
{
    public bool WasInvoked { get; private set; }

    public ValueTask<Result> AuthorizeAsync(IRequestContext<GuardedAndAuthorizedAction> context, CancellationToken ct)
    {
        WasInvoked = true;
        return ValueTask.FromResult(grant
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden, "Denied by authorizer.")));
    }
}

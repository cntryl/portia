# Getting started

Portia targets .NET 10. Most applications reference `Portia.Abstractions`,
`Portia.DependencyInjection`, and the `Portia.Generators` analyzer. Add
`Portia.AspNetCore` for HTTP, `Portia.Fitz` for Fitz storage/transports, and
`Portia.Jwt` when inbound work carries JWT actor identities. Packages use the
cntryl GitHub Packages feed at `https://nuget.pkg.github.com/cntryl/index.json`.

## Define contracts and register components

```csharp
[RequestRoute("consumer", "business", "*", "deposit")]
public sealed record DepositAccount(Uuid Id, int Amount) : IRequest, ICallable, IQueuable;

public sealed record Deposited(int Amount) : DomainEvent;
public sealed record Declined(string Reason) : DomainEvent;
```

Reference the generator in each assembly declaring handlers, authorizers, routed requests,
reactors, or projectors, and in the host that maps HTTP endpoints.

Select handlers and authorizers through the stable generic methods `AddRequestHandler<T>()`
and `AddRequestAuthorizer<T>()`. The generator replaces each call
with its typed descriptor and reports a compile error when the type does not implement that role.
Projectors and reactors are selected with
`AddProjector<T>(...)` and `AddReactor<T>(...)`, which also choose their execution scope.

Registering a handler also registers its request's transport descriptor. A contract used only
by a sending application is inferred from strongly typed `SendAsync` and `StreamAsync` calls.
Use `RegisterDynamicRequest<T>()` only when dynamic dispatch hides the concrete request type
from the compiler. The generator contributes referenced domain
events automatically because an event missing from the catalog is always a replay-time failure,
not a deployment choice. `AddEvent<T>()` remains a low-level escape hatch for a type unavailable
to the generator.

A dispatch-only application therefore needs no component lambda:

```csharp
services.AddPortia();
```

Repeated identical registrations are idempotent; conflicting selected handlers or conflicting
stages for the same authorizer fail during registration. Multiple applicable authorizers compose
as an all-of pipeline. Unselected types are not added to the application.

Generated descriptors retain typed dispatch and permission expressions. There is no runtime
assembly scanning or reflection—the call-site generator emits concrete generic descriptors, so
the whole registration path is visible to the compiler and to trimming.

Applications may keep feature-specific `IServiceCollection` extensions as ordinary composition
helpers, but Portia no longer requires assembly wrappers around generated method names.

A scoped `IRequestBus` resolves the selected handler and authorizer from the current
scope. A handler may inject that bus to dispatch a different request. Permission
checks run before authorizers, which run before the handler. No application bus or
runtime assembly scan is required.

Authorization is independently composable. An `IRequestAuthorizer<TScope>` may target one
concrete request, a request-family interface, or `IRequestBase`; every selected authorizer whose
scope matches the concrete request runs before its handler. Register broad principal policy,
resource access, and step-up checks once with `AddRequestAuthorizer<T>(AuthorizationStage)`.
All applicable policies must succeed and the first failure short-circuits handling.

```csharp
public interface IAccountRequest : IRequestBase { Uuid AccountId { get; } }
public interface IMfaConfirmedRequest : IRequestBase;

public sealed record CloseAccount(Uuid AccountId)
    : IRequest, IAccountRequest, IMfaConfirmedRequest;

services.AddPortia(portia =>
{
    portia.AddRequestHandler<CloseAccountHandler>();
    portia.AddRequestAuthorizer<ActiveUserAuthorizer>(AuthorizationStage.Principal);
    portia.AddRequestAuthorizer<AccountRoleAuthorizer>(AuthorizationStage.ResourceAccess);
    portia.AddRequestAuthorizer<MfaConfirmationAuthorizer>(AuthorizationStage.StepUp);
});
```

Here `ActiveUserAuthorizer` implements `IRequestAuthorizer<IRequestBase>`, the account policy
implements `IRequestAuthorizer<IAccountRequest>`, and the MFA policy implements
`IRequestAuthorizer<IMfaConfirmedRequest>`. Registration order breaks ties within a stage;
authorization policy should otherwise avoid order-dependent side effects.

## Persist an aggregate

Use `Uuid.CreateVersion4()` for a new random identity, or `Uuid.CreateVersion5(namespaceId, name)` when the same name must produce the same identity. Portia orders events using stream positions and aggregate versions; UUIDs do not define event order.

Construction always receives an explicit identity and defines its stream address:

```csharp
public sealed class Account : Aggregate
{
    public Account(Uuid id)
        : base(id, new EventStreamAddress("consumer", "accounts", id.ToString()))
    {
        On<Deposited>(ev => Balance += ev.Amount);
    }

    public int Balance { get; private set; }
    public void Deposit(int amount) => RaiseEvent(new Deposited(amount));

    // Audits live in their own stream and cannot be committed in the same transaction as
    // raised events, so save one before emitting the other.
    public void Decline(string reason) => AuditEvent(new Declined(reason));
}
```

Configure persistence once in the shared application setup:

```csharp
services.AddPortia(portia =>
{
    portia.AddRequestHandler<DepositAccountHandler>();
});
services.AddPortiaFitz(configuration.GetSection("Fitz"));
```

Inject `IAggregateRepository` into a handler and construct the aggregate normally:

```csharp
var account = await repository.HydrateAsync(new Account(id), ct);
account.Deposit(amount);
await repository.SaveAsync(account, context, ct);
```

The same instance can be hydrated again later. Reads start at its committed stream
position and apply only newer raised events. A missing stream leaves it unchanged.
Save pending changes before refreshing; do not mutate the instance during hydration.
No aggregate factory or aggregate registration is required. Constructor dependencies
are passed by the application. See the
[shared API and worker setup](application-setup.md) for two deployments
using one application configuration.

A pending save contains raised events **or** audits. Raising applies state and
advances `Version` immediately. Auditing applies no state. Raised events append to
the aggregate's stable stream using `CommittedStreamPosition` for OCC. An audit
batch appends to a fresh UUIDv4 session stream in the same realm/area, retaining the
aggregate identity and state version in metadata. It cannot compete with a command
on the aggregate stream. A failed save retains pending events and its audit session
identity; a successful save clears pending changes. A new audit batch gets a new
session UUID. An audit before the first raised event leaves aggregate loading absent.

Do not emit or save concurrently on one aggregate instance. An OCC conflict throws
`EventStreamConcurrencyException`; Portia does not rerun business commands.

## Map HTTP endpoints

The installed `Portia.Generators` package supplies the interceptor namespace.
Reference it directly in the HTTP host with `PrivateAssets="all"`; no manual
`InterceptorsNamespaces` property is needed. Repository project references used to
develop the generator still need their local compiler configuration.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPortia(portia => portia.AddRequestHandler<DepositAccountHandler>());
// Register persistence and application dependencies as above.
var app = builder.Build();
app.MapPortiaPost<DepositAccount>("/accounts/{id}");
app.Run();
```

A constructor parameter matching a route token binds from that route. Remaining
parameters bind from the query for GET/DELETE, or an object JSON body for
POST/PUT/PATCH. Missing nullable parameters become null, omitted optional parameters
use their declared default, and missing required values return 400. Explicit JSON
null requires a nullable parameter. Invalid root/value kinds return 400.

JSON binding uses ASP.NET HTTP JSON options, including application converters,
property metadata, and naming. The default is camel case. To retain snake case:

```csharp
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
```

Route/query scalars use invariant parsing. Mapping routes must be compile-time
constants; unsupported binding shapes produce `PORTIA016` diagnostics. Requests
remain independent of ASP.NET attributes.

For a no-result `IQueuable` request, `Prefer: respond-async` selects queue publishing
and returns 202. Register `IRequestQueuePublisher`; supply wildcard route values
explicitly on the endpoint:

```csharp
app.MapPortiaPost<DepositAccount>("/accounts/{id}")
    .WithPortiaRouteValues(context => new RequestRouteValues(
        Resource: context.Request.RouteValues["id"]!.ToString()));
```

The request's configured concrete route segments remain authoritative. For a
wildcard realm, derive `Realm` from the application's authenticated tenant context
in this resolver. The original bearer token is carried to the queue and validated
again when the work executes.

## Run transports and components

Keep Fitz connections long-lived. Register `IRequestDeserializer`,
`IRequestOutcomeSerializer`, and `IRequestActorValidator`. The RPC server resolves
these and the bus inside a fresh scope per invocation:

```csharp
var server = new FitzRpcRequestServer(fitz.Rpc,
    provider.GetRequiredService<IServiceScopeFactory>());
await using var workers = await server.RegisterRequestsAsync(ct);
// Keep workers alive until the host shuts down.
```

The composed descriptors retain each request's result type. Contracts need no Fitz
reference. The returned handle owns every worker and unregisters them on disposal.

Queue and notification hosting create a scope for each delivery, including nested
dispatch, and dispose it on completion, failure, or cancellation:

```csharp
services.AddSingleton<IRequestQueueConsumer>(new FitzRequestQueueConsumer(
    fitz.Queue, serializer, "queue://consumer/business/account-id"));
services.AddPortiaQueueRunner();
```

Queue polling defaults to a five-second wait and one reserved item. Active work
renews its Fitz reservation. Malformed or unexpectedly failed requests stop renewal
and remain unacknowledged; Fitz controls expiration, redelivery, and configured
dead-letter policy. Hosted stream failures reconnect after backoff. This does not
republish failed messages or add application retry counters.

Register workloads in shared application setup, then activate the worker deployment:

```csharp
services.AddPortia(portia =>
{
    portia.AddProjector<AccountProjector>(o => o.PerTenant());
    portia.AddProjector<PlatformSummaryProjector>(o => o.Global());
    portia.AddReactor<AccountReactor>(o => o.PerTenant());
}).AddWorker();
```

Fitz coordinates these registrations across replicas. `PerTenant()` requires an
`ITenantDirectory`; `Global()` retains the component's declared realm and filters.
See [shared application setup](application-setup.md) for application identity and fencing.

Inject ordinary application repositories into your processors. Projectors pass a repository
implementing `IProjectionStore` into `BaseProjector` or `BaseBatchProjector`; reactors pass
a dependency implementing `IProjectionCheckpointStore` into `BaseReactor` or `BaseBatchReactor`.
The same repository can implement framework persistence and application operations.

Each worker pass uses a fresh scope. Projection changes and progress commit atomically
through `IProjectionBatch`. Single-event bases advance progress per event; batch bases
advance it after each successful bounded batch. Reactor effects remain at-least-once.
Persist the full checkpoint identity: component name, canonical pattern, and optional
rebuild ID. Reload authoritative progress after an uncertain commit. See the
[processor guide](projectors-and-reactors.md) for complete constructor examples.

The event-sourced tenant directory and tenant restarts default to one second and
accept `TimeProvider`. Each watcher owns independent progress. Failed active tenant
workloads restart in new scopes; removal and shutdown cancel execution and backoff.

## Rebuilds and fleet hosting

A rebuild uses an explicit generation ID:

```csharp
portia.AddProjector<AccountProjector>(o =>
{
    o.PerTenant();
    o.Processing = new ProjectionRunOptions { RebuildId = "accounts-2026-09", MaxBatchSize = 512 };
});
```

The repository must use the complete checkpoint identity to select both data and progress.
A new ID has no checkpoint and starts at zero; reusing an ID resumes its committed batches.
Live processing has a null ID. Handlers still read `context.IsRebuild`, derived from the ID.
Keep live and rebuilt data separate; the application decides when and how to promote rebuilt
results. Do not seed rebuild progress from live progress.

Register one shared Fitz `ILeaseClient`, the scoped partition workload, and fleet options:

```csharp
services.AddScoped<AccountPartitionWorkload>();
services.AddPortiaFleetPartitionRunner<AccountPartitionWorkload>(
    ["lease://accounts/partitions/0", "lease://accounts/partitions/1"],
    new FleetRunOptions { MembershipSelector = "lease://accounts/workers/*" });
```

`AccountPartitionWorkload` implements `IPartitionWorkload`. Each acquired lease gets its own
scope and `LeaseAuthority`; downstream writes must enforce its fencing token. The membership
area must not contain partition leases or unrelated leases. All workers in the fleet must use
identical selectors and partition sets. An omitted worker ID becomes one UUIDv4 per run,
retained across reconnects; explicit IDs must be unique among live workers.

Membership and partition TTLs default to 30 seconds; positive fractional TTLs round upward to
whole seconds. Snapshot reconciliation defaults to one second. Rendezvous hashing gives stable
assignments with minimal movement on joins/departures, without guaranteeing equal counts.
Membership loss or an unready inventory cancels partition work. Membership faults retry after
a cancellable one-second backoff through `TimeProvider`. Logs and tracing report membership
faults and assignment changes.

## Authorization and streaming

Use `[RequiresPermission("orders:{OrderId}:read")]` and register an
`IPermissionEvaluator`. Add `IRequestAuthorizer<T>` for entity-specific decisions.
Every direct bus call supplies its actor explicitly; HTTP supplies `HttpContext.User`.
Transport actor validation happens inside the delivery scope.

`MapPortiaGetStream<TRequest, TOut>` writes an incremental JSON array;
`MapPortiaGetSse<TRequest, TOut>` writes SSE. Both enumerate once and check the first
move before starting the response, so authentication/authorization failures become
401/403. A failure after streaming begins logs the error and aborts the response.
Cancellation and failures dispose the enumerator and its scope.

## Use the maintained consumer fixture

[CompleteWorkflowTests](../test/Portia.ConsumerTests/CompleteWorkflowTests.cs) assembles
two feature assemblies, scoped persistence, two reactors and two projectors. The
same business handler runs through direct dispatch, generated HTTP, real Fitz RPC
and queue delivery. It checks aggregate state, durable source and audit streams,
and derived results separately, against both event stores.

Use `AggregateScenario<T>` to inspect pending/committed changes and seed raised
history in business tests. Use `DomainEventSeed.Attach` to seed store events.
Neither requires reflection nor `InternalsVisibleTo`. Run the solution against
Docker Compose using the commands in the [README](../README.md). Review the
[migration notes](migration.md) before upgrading existing applications.

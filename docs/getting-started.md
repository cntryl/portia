# Getting started

Portia targets .NET 10. Most applications reference `Portia.Abstractions` and
`Portia.DependencyInjection`, which includes the compile-time generator. An HTTP host may
reference only `Portia.AspNetCore`: it depends directly on `Portia.DependencyInjection`, so the
registration APIs, generator/analyzer assets, and interceptor compiler configuration arrive
transitively. Add `Portia.Fitz` for Fitz storage/transports and `Portia.Jwt` when inbound work
carries JWT actor identities. Packages use the
cntryl GitHub Packages feed at `https://nuget.pkg.github.com/cntryl/index.json`.
Add the optional `Portia.Telemetry` package and call `AddOpenTelemetry().WithPortia()` when the
application wants Portia's traces, metrics, and structured logs registered with OpenTelemetry.
Portia does not select an exporter, resource, sampling policy, filter, endpoint, or credential.
Review the [scope](scope.md) page for what Portia supports and the
[design decisions](design-decisions.md) behind its operational boundaries.

The repository's `NuGet.Config` already maps `Cntryl.*` packages to that feed and reads its
credentials from the environment. Before `dotnet restore`, set your GitHub username and a
[classic personal access token with `read:packages`](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry):

```sh
export GITHUB_ACTOR=your-github-username
export GITHUB_TOKEN=your-classic-personal-access-token
dotnet restore
```

Do not commit the token. A 401 usually means the username/token is missing, expired, or is not a
classic PAT; a 403 usually means the token lacks `read:packages`, requires organization SSO
authorization, or the account cannot read the package. In GitHub Actions, prefer the workflow's
`GITHUB_TOKEN` when its repository has package read access; otherwise use a `read:packages`
classic PAT stored as an Actions secret.

## Concepts in one minute

A **request** asks the application to do work, and one selected **handler** authorizes and
executes that request. A **projector** consumes domain events to update application-owned read
models; a **reactor** consumes them to cause idempotent external effects. A **workload** is one
registered projector, reactor, or partition job running in its selected global or per-tenant
scope. A **stream address** identifies an ordered event stream by realm, area, and resource. A
**checkpoint** records how far a processor has committed progress so it can resume without
claiming uncommitted events.

## Define contracts and register components

```csharp
[Discriminator("consumer.business.deposit-account")]
[RequestRoute("consumer", "business", "*", "deposit")]
public sealed record DepositAccount(Uuid Id, int Amount) : IRequest, ICallable, IQueuable;

[Discriminator("Deposited")]
public sealed record Deposited(int Amount) : DomainEvent;
[Discriminator("Declined")]
public sealed record Declined(string Reason) : DomainEvent;

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DepositAccount))]
[JsonSerializable(typeof(Deposited))]
[JsonSerializable(typeof(Declined))]
internal sealed partial class BusinessJsonContext : JsonSerializerContext;
```

Portia uses snake_case for application JSON by default, including HTTP. Keep the source-generation
option aligned with that default. Use `ConfigureJson` only when the whole application deliberately
chooses a different convention or adds converters.

`PORTIA025` catches JSON roots that the generator can discover at compile time. Startup validation
remains the defensive fallback for roots and resolver combinations that cross compilation
boundaries or otherwise cannot be proven statically.

Reference `Portia.DependencyInjection` in each assembly that registers handlers, authorizers, pipeline behaviors,
or routed requests. The generator and interceptor configuration arrive with that package.
Because libraries and applications can compose components across assemblies, Portia cannot prove
that every declared handler has been registered; the application composition root owns that
selection.

Registration calls are rewritten at the call site, so each one must appear literally where you
compose the application. They cannot be wrapped in a helper that takes the component as a type
parameter, passed as a delegate, or invoked through reflection — the generator has no call site
to intercept in those forms, and the call throws at startup (`PORTIA019` catches the shapes it
can see at compile time). Grouping registrations in an ordinary extension method is fine; it is
only forwarding the *generic argument* that does not work.

Select handlers, authorizers, and pipeline behaviors through the stable generic methods
`AddRequestHandler<T>()`, `AddRequestAuthorizer<T>()`, and `AddRequestPipelineBehavior<T>(order)`.
The generator replaces each call
with its typed descriptor and reports a compile error when the type does not implement that role.
Projectors and reactors are selected with
`AddProjector<T>(...)` and `AddReactor<T>(...)`, which also choose their execution scope.

Registering a handler also registers its request's transport descriptor. A contract used only
by a sending application is inferred from strongly typed `SendAsync`, `StreamAsync`,
`EnqueueAsync`, `PublishAsync`, and `ScheduleAsync` calls.
Use `RegisterDynamicRequest<T>()` only when dynamic dispatch hides the concrete request type
from the compiler. The generator contributes domain events declared by the registering assembly
and external event types that assembly actually uses. Merely referencing a package does not add
all of its events to the application catalog. `AddEvent<T>()` remains a low-level escape hatch for
a type hidden from compile-time analysis.

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
scope. A handler may inject that bus to dispatch a different request. Principal policies run
first; declarative permission checks begin resource access, followed by resource and step-up
authorizers, then the handler. No application bus or runtime assembly scan is required.

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

services.AddPortia()
    .AddRequestHandler<CloseAccountHandler>()
    .AddRequestAuthorizer<ActiveUserAuthorizer>(AuthorizationStage.Principal)
    .AddRequestAuthorizer<AccountRoleAuthorizer>(AuthorizationStage.ResourceAccess)
    .AddRequestAuthorizer<MfaConfirmationAuthorizer>(AuthorizationStage.StepUp);
```

Here `ActiveUserAuthorizer` implements `IRequestAuthorizer<IRequestBase>`, the account policy
implements `IRequestAuthorizer<IAccountRequest>`, and the MFA policy implements
`IRequestAuthorizer<IMfaConfirmedRequest>`. Registration order breaks ties within a stage;
authorization policy should otherwise avoid order-dependent side effects.

Pipeline behaviors wrap handler execution after every applicable authorization policy succeeds.
Implement `IRequestPipelineBehavior<TRequest>` for commands,
`IRequestPipelineBehavior<TRequest,TOut>` for queries, or
`IStreamRequestPipelineBehavior<TRequest,TOut>` for streams. A behavior can target a concrete
request or request-family interface. Lower orders are outermost; registration order breaks ties.
An authorization failure never enters the behavior chain. Every behavior must either return a
fully initialized `Result` or call its typed `continuation`; Portia names the responsible behavior
or handler when an uninitialized result crosses the framework boundary. A behavior may invoke its
continuation zero or one time and may not retain it after the behavior invocation returns. A
second, concurrent losing, or late invocation throws `InvalidOperationException`; awaiting before
the first call remains valid. The same rule applies to result-bearing and streaming behaviors.

For a successful `Result<T>`, `Value` has exactly the nullability declared by `T`.
`Result<string>.Value` is therefore non-nullable, while `Result<string?>.Success(null)` is valid
and exposes a nullable value. Reading `Value` from a failed result throws; reading any member from
`default(Result<T>)` throws because no outcome was produced.

Behaviors are where cross-cutting concerns belong: a transaction or unit of work around the
handler, an idempotency check, retry, caching, flushing an outbox, or logging that needs the
outcome as well as the input. Anything that must run *after* the handler has no other home —
`IRequestAuthorizer` runs before it and can only permit or deny. A behavior matched to a
request-family interface applies to every request in that family, so the concern is written once
rather than repeated per handler. A behavior selected by scope but unable to serve the dispatched
shape — a no-result behavior reached through a result-bearing dispatch — is skipped, not an
error.

```csharp
services.AddPortia()
    .AddRequestHandler<CloseAccountHandler>()
    .AddRequestPipelineBehavior<AccountAuditBehavior>(order: 100);
```

## Persist an aggregate

Use `Uuid.CreateVersion4()` for a new random identity, or `Uuid.CreateVersion5(namespaceId, name)`
when the same name must produce the same identity. `Uuid` supplies consistent UUID semantics and
deterministic version-5 generation that the target BCL API does not provide. It implements `ISpanParsable<Uuid>`,
`ISpanFormattable`, and `IComparable<Uuid>` by following the wrapped `Guid` behavior, so generic
binding and allocation-conscious formatting do not require an adapter. Portia orders events
using stream positions and aggregate versions; UUIDs do not define event order.

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
services.AddPortia()
    .AddRequestHandler<DepositAccountHandler>()
    .AddFitz(configuration.GetSection("Fitz"));
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

The HTTP application automatically exposes OpenAPI 3.1 at `/openapi/v1.json` and
`/openapi/v1.yml`; no `AddOpenApi()`, `MapOpenApi()`, or endpoint annotations are required.
Portia describes generated bindings while ordinary minimal-API endpoints remain governed by
Microsoft's standard generator. To download YAML:

```sh
curl http://localhost:5000/openapi/v1.yml --output openapi.yml
```

### Server-sent event streams

`MapPortiaGetSse` frames each item as one event. A `string` item is written as-is, since the body is
text and the document declares the item's own schema for it; anything else is written as JSON. A
payload containing line breaks becomes one `data:` field per line.

An idle stream emits a comment every `PortiaHttpOptions.ServerSentEventKeepAlive` (15 seconds by
default), which every client ignores and every intermediary counts as traffic. Set it to `null` to
send none:

```csharp
builder.Services.Configure<PortiaHttpOptions>(options => options.ServerSentEventKeepAlive = null);
```

Portia does not emit `id:` or `retry:`, so a client reconnecting after a drop resumes from wherever
the request's own parameters place it, not from a `Last-Event-ID`.

### Choosing where the document is served

Both routes are served in every environment by default, and neither requires authorization.
Publishing a schema is not itself a disclosure — every mapped endpoint is one the application opted
into with `ICallable` — but whether it should be publicly reachable is a deployment decision, so
`PortiaHttpOptions.ServeOpenApi` withdraws Portia's two routes:

```csharp
builder.Services.Configure<PortiaHttpOptions>(options => options.ServeOpenApi = false);
```

Turning serving off does not stop the document from being composed. The application can map it
wherever it wants, and gets the same document — Portia's operation IDs, parameters, and responses
included. An application that maps the document itself is calling ASP.NET Core's own `MapOpenApi`,
which composes it per request; the caching described below applies to Portia's routes:

```csharp
// Development only, matching the ASP.NET Core template's own default.
builder.Services.Configure<PortiaHttpOptions>(options =>
    options.ServeOpenApi = builder.Environment.IsDevelopment());

// Or serve it yourself, behind whatever the rest of the application uses.
builder.Services.Configure<PortiaHttpOptions>(options => options.ServeOpenApi = false);
...
app.MapOpenApi("/internal/openapi/{documentName}.json").RequireAuthorization("ops");
```

Composing the document is not cheap: it walks every described endpoint and resolves a schema for
each parameter and response. Because endpoints are fixed once the host starts, Portia composes it
on the first request and serves the rendered bytes from then on, so later requests only copy them.
Composition happens on first use rather than during startup, which keeps the cost off the critical
path of a host that never serves the document. A document that cannot be composed — a duplicated
operation ID, say — fails every request rather than being cached as a failure.

`Portia.AspNetCore` depends directly on `Portia.DependencyInjection`, which supplies the generator
and interceptor namespace to the HTTP host. A host referencing only the HTTP package therefore
needs no separate dependency-injection/analyzer package or `InterceptorsNamespaces` property.
Repository project references receive the analyzer directly from the dependency-injection project.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPortia().AddRequestHandler<DepositAccountHandler>();
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

An absent body is treated as `{}` only when every body member is optional. JSON bodies
are bounded to 10 MiB by default; configure `PortiaHttpOptions.MaxJsonBodyBytes` through
standard options registration. Exceeding the bound returns `413 application/problem+json`.
Bodies are buffered completely and must be JSON objects; multipart, form, binary, and streaming
request-body shapes are unsupported.
Problem responses contain RFC 9457 `type`, `title`, `status`, `detail`, and `instance` members.
Expected request failures also include a Boolean `transient` extension and matching
`Portia-Transient` response header, preserving `RequestError.IsTransient` without inventing a
retry delay. Unauthorized results remain bodyless to avoid leaking authentication details and
send `WWW-Authenticate: Bearer`.
Query names are case-insensitive through ASP.NET Core's query collection, while repeated
scalar values are rejected as ambiguous.

JSON binding uses Portia's frozen source-generated JSON options, including application converters,
property metadata, and naming. Configure them before building the provider:

```csharp
builder.Services.AddPortia().ConfigureJson(options =>
    options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
```

Route/query scalars use invariant parsing. Mapping routes must be compile-time
constants; unsupported binding shapes produce `PORTIA016` diagnostics. Requests
remain independent of ASP.NET attributes.

For a no-result `IQueuable` request, the exact `Prefer: respond-async` token selects queue
publishing and returns 202 with `Preference-Applied: respond-async` and a JSON request-ID
receipt. The request's declared authorization runs before it is accepted, so a caller who would
receive 403 synchronously receives 403 here too and nothing reaches the queue. The worker
re-validates the carried actor token and authorizes again when it runs the request. The ID is correlation identity, not completion tracking. Portia has no status
resource and therefore emits no `Location` header. Register `IRequestQueuePublisher`; supply wildcard route values
explicitly on the endpoint:

```csharp
app.MapPortiaPost<DepositAccount>("/accounts/{id}")
    .WithPortiaRouteValues(context => new RequestRouteValues(
        Resource: context.Request.RouteValues["id"]!.ToString()));
```

The request's configured concrete route segments remain authoritative. For a
wildcard realm, derive `Realm` from the application's authenticated tenant context
in this resolver. One well-formed, nonempty Bearer credential may be carried to the queue
and is validated again when the work executes. Authentication schemes and token validation
remain application-owned.

Generated endpoints return the advertised RFC 9457 problem body for binding
and unexpected failures before a response starts. Once streaming output has started, HTTP
cannot replace it with a problem response; Portia logs the failure and aborts the connection.

## Run transports and components

Configure Fitz once and declare request workers from the selected handlers. Only a deployment
that calls `AddWorkers()` starts those workers:

```csharp
services.AddPortia()
    .AddRequestHandler<DepositAccountHandler>()
    .AddProjector<AccountProjector>(WorkloadScope.PerTenant)
    .AddReactor<AccountReactor>(WorkloadScope.PerTenant)
    .AddFitz(configuration.GetSection("Fitz"))
    .AddWorkers();
```

The default `AddFitz()` declaration includes each RPC, queue, notice, and schedule transport exposed
by a selected handler, including when its callback only configures fleet membership. The first
`AddRpcWorkers()`, `AddQueueWorkers()`, `AddNoticeWorkers()`, or `AddScheduledWorkers()` call narrows
that default; later calls add transport kinds to the selection. Use `DisableRequestWorkers()` for a
workload-only deployment. An explicitly selected transport must match at least one selected handler
or worker startup fails before Fitz connects. Outbound-only inferred requests never become listeners.

Declare durable cron schedules in the same shared setup. They remain dormant in API-only hosts
and are upserted sequentially whenever a host that calls `AddWorkers()` starts:

```csharp
services.AddPortia()
    .AddRequestSchedule(new ReconcileAccounts(), new RequestScheduleSpec("0 */5 * * *"),
        RequestRouteValues.None, RequestActor.CreateSystem("account-scheduler"))
    .AddFitz(configuration.GetSection("Fitz"))
    .AddWorkers();
```

`AddRequestSchedule` is declarative desired state; commands and reactors continue to call
`IRequestScheduler.ScheduleAsync` for causal scheduling. Fitz uses the resolved concrete route as
the schedule and cancellation identity. Reapplying an identical definition keeps its firing cursor;
changing it at the same route uses Fitz's native last-write-wins upsert. Removing a declaration does
not cancel it: explicitly call `IScheduleClient.CancelAsync` with the concrete route. The native
client is available from DI for listing and administration over Portia's shared Fitz connection.
A durable schedule definition produces live, non-backlogged Fitz firing deliveries; use a queue
when every missed delivery must remain pending.

Keep Fitz connections long-lived and register an `IRequestActorValidator` for inbound work.
Fitz supplies the request serializers and creates a fresh dependency-injection scope for each
invocation or delivery.

### Advanced transport hosting

Applications that deliberately own individual consumers can construct `FitzRpcRequestServer`,
`FitzRequestQueueConsumer`, and the low-level runners directly. Do not combine manual hosting
with `AddRequestWorkers()` for the same routes.

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

Projector and reactor passes use a separate bounded policy: failures leave the checkpoint
unchanged, retry with exponential backoff, and fault the worker with `WorkloadFailureException`
after ten consecutive attempts by default. Configure `FailureAttemptLimit` and
`MaximumFailureDelay` on the component's `WorkloadOptions`; successful passes reset the policy.

Register workloads in shared application setup, then activate the worker deployment:

```csharp
services.AddPortia()
    .AddProjector<AccountProjector>(WorkloadScope.PerTenant)
    .AddProjector<PlatformSummaryProjector>(WorkloadScope.Global)
    .AddReactor<AccountReactor>(WorkloadScope.PerTenant)
    .AddWorkers();
```

Fitz coordinates these registrations across replicas. `WorkloadScope.PerTenant` requires an
`ITenantDirectory`; `WorkloadScope.Global` retains the component's declared realm and filters.
See [shared application setup](application-setup.md) for application identity and fleet coordination.

Inject ordinary application repositories into your processors. Projectors pass a repository
implementing `IProjectionStore` into `Projector` or `BatchProjector`; reactors pass
a dependency implementing `IProjectionCheckpointStore` into `Reactor` or `BatchReactor`.
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
portia.AddProjector<AccountProjector>(WorkloadScope.PerTenant, o =>
{
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
scope and runs until Fitz reports lease loss or shutdown through cancellation. The membership
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
Hosted startup fails before serving work if guarded handlers are selected without an evaluator,
including guarded handlers contributed by another feature assembly. A host with no guarded
request does not require one.

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
Docker Compose using the commands in the [README](../README.md).

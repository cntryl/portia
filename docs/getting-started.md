# Getting started

Portia targets .NET 10. Most applications reference `Portia.Abstractions`,
`Portia.DependencyInjection`, and the `Portia.Generators` analyzer. Add
`Portia.AspNetCore` for HTTP, `Portia.Fitz` for Fitz storage/transports, and
`Portia.Jwt` when inbound work carries JWT actor identities. Packages use the
cntryl GitHub Packages feed at `https://nuget.pkg.github.com/cntryl/index.json`.

## Define contracts and explicit modules

Each feature assembly declares a named partial module. A separate contracts
assembly can declare its own module, which features explicitly import:

```csharp
// Contracts assembly
[PortiaModule]
public partial class ContractsModule;

[RequestRoute("consumer", "business", "*", "deposit")]
public sealed record DepositAccount(Uuid Id, int Amount) : IRequest, ICallable, IQueuable;

public sealed record Deposited(int Amount) : DomainEvent;
public sealed record Declined(string Reason) : DomainEvent;

// Feature assembly
[PortiaModule(typeof(ContractsModule))]
public partial class AccountsModule;
```

Reference the generator as an analyzer in each assembly that declares modules or
components, and in the host that maps HTTP endpoints. The generated module adds
its handlers, authorizers, event catalog entries, transport descriptors, reactors,
and projectors. Imports and repeated `AddPortiaModule<T>()` calls are idempotent.
Conflicting handlers or authorizers fail explicitly during registration.

A scoped `IRequestBus` resolves the selected handler and authorizer from the current
scope. A handler may inject that bus to dispatch a different request. Permission
checks run before authorizers, which run before the handler. No application bus or
runtime assembly scan is required.

## Persist an aggregate

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
    public void Decline(string reason) => AuditEvent(new Declined(reason));
}
```

Register a factory for loading and an event store. The factory receives the current
service provider and UUID, so metadata factories and other construction dependencies
can come from the caller's scope:

```csharp
services.AddPortiaModule<AccountsModule>();
services.AddPortiaAggregate<Account>((provider, id) => new Account(id));
services.AddSingleton<IEventStore>(provider =>
    new FitzEventStore(fitz.Stream, provider.GetRequiredService<IDomainEventSerializer>()));
services.AddSingleton<IDomainEventReader>(provider => provider.GetRequiredService<IEventStore>());
```

Inject nongeneric `IAggregateRepository` into a handler. Use
`LoadAsync<Account>(id, ct)`, create `new Account(id)` when absent, perform the
business operation, and `SaveAsync(account, ct)`. The
[fixture handler](../test/Consumer.FeatureOne/DepositAccountHandler.cs) demonstrates
both successful deposits and declined operations.

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

Enable the generator's interceptor namespace in the host project:

```xml
<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Cntryl.Portia.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPortiaModule<AccountsModule>();
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
await using var workers = await server.RegisterModulesAsync(ct);
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

Register projector and reactor hosting by the **concrete component type** after
its module, even when projectors share a projection-port type:

```csharp
services.AddPortiaModule<AccountsModule>();
services.AddPortiaModule<ReportingModule>();
services.AddPortiaProjectorRunner<FirstProjector>();
services.AddPortiaProjectorRunner<SecondProjector>();
services.AddPortiaReactorRunner<FirstReactor>();
services.AddPortiaReactorRunner<SecondReactor>();
```

Provide `IProjectionTarget<T>` for projectors and `IProjectionCheckpointStore` for
reactors. Each pass uses a fresh scope. A projection batch must atomically commit
its projection changes and checkpoint. Each pass reloads the durable checkpoint,
including after an uncertain commit. Pattern readers and components can consume
raised events and audits; aggregate rehydration reads only its stable source stream.

The event-sourced tenant directory and tenant restarts default to one second and
accept `TimeProvider`. Each watcher owns independent progress. Failed active tenant
workloads restart in new scopes; removal and shutdown cancel execution and backoff.

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

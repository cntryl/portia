# Portia

Write a command and its handler:

`CreateGreeting.cs`:

```csharp
using Cntryl.Portia;

[RequestRoute("public", "greetings", "messages", "create")]
[Discriminator("greetings.create")]
public sealed record CreateGreeting(string Name) : IRequest<string>, ICallable;

sealed class CreateGreetingHandler : IRequestHandler<CreateGreeting, string>
{
    public ValueTask<Result<string>> HandleAsync(
        IRequestContext<CreateGreeting> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success($"Hello, {context.Request.Name}!"));
}
```

Declare the JSON roots the application owns:

`ApplicationJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;
using Cntryl.Portia;

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CreateGreeting))]
[JsonSerializable(typeof(string))]
internal sealed partial class ApplicationJsonContext : JsonSerializerContext;
```

Wire the command to an HTTP route:

`Program.cs`:

```csharp
using Cntryl.Portia;

var builder = WebApplication.CreateBuilder(args);
_ = builder.Services.AddPortia()
    .AddRequestHandler<CreateGreetingHandler>();

var app = builder.Build();
app.MapPortiaPost<CreateGreeting, string>("/greetings");
app.Run();
```

Call it:

```console
$ curl -X POST http://localhost:5000/greetings \
    -H 'Content-Type: application/json' \
    -d '{"name":"Portia"}'
"Hello, Portia!"
```

That's it. The command holds the input, the handler does the work, and the route makes it
available over HTTP.

## API and worker deployments

Declare components, persistence, and background work once in a shared application
project. The API host calls `AddAccountsApplication(configuration)`; the worker host
calls the same setup followed by `.AddWorkers()`. HTTP traffic and background work
can scale independently. See [shared application setup](docs/application-setup.md)
for connection ownership, mixed global/per-tenant workloads, and incremental aggregate hydration.

## Why it works

- `IRequest<string>` says that `CreateGreeting` returns text.
- `ICallable` says that the application may expose it to callers, including over HTTP.
- `[Discriminator("greetings.create")]` is the stable wire identity used by non-HTTP
  transports. It is independent of the route and never derived from a CLR type name.
- `[PortiaJsonContext]` composes the application's normal System.Text.Json source-generated
  metadata with Portia's built-in metadata. Add each transported request, event, and result root
  to one of the application's partial contexts.
- `IRequestHandler<CreateGreeting, string>` connects that command to its handler.
- `AddRequestHandler<CreateGreetingHandler>()` adds the command and handler to the application.
  Portia.Generators validates the role and emits its typed descriptor at the call site, so naming
  a type that isn't a handler is a compile error, not a startup failure.
- `MapPortiaPost` reads the HTTP request, calls the handler, and writes the HTTP response.

Portia writes the repetitive connection code when the project builds. It is ordinary C# checked
by the compiler—the application explicitly selects its components, with no assembly scanning and
no reflection: `AddRequestHandler<CreateGreetingHandler>()` is replaced at compile time with a typed descriptor.
If the command and handler disagree about their types, the build fails.

## How we keep it simple

The handler only knows about the command. It does not know about HTTP, queues, or any other way
the command might arrive. That lets the same handler run without being copied or wrapped when the
application adds another way to call it.

Every command follows the same path to its handler. Permissions, identity checks, and tracing can
be added to that path once instead of being repeated in every endpoint. A command explicitly opts
into the ways it may be called, so behavior stays visible in its type rather than hidden in setup
code.

[Read the getting-started guide](docs/getting-started.md) for package setup, authorization,
asynchronous work, persistence, and streaming. The [consumer fixture](test/Portia.ConsumerTests/CompleteWorkflowTests.cs)
executes two feature assemblies through direct dispatch, HTTP, RPC, and queues.

## Development

To run the test suite:

```
docker compose up --detach fitz
dotnet format Portia.slnx --verify-no-changes
dotnet build Portia.slnx --configuration Release
dotnet test Portia.slnx --configuration Release --no-build
docker compose down --volumes
```

The Fitz integration and public consumer tests connect to the Compose-managed broker at
`ws://127.0.0.1:4090/ws` by default; override `FITZ_TEST_ENDPOINT` when using another
broker. CI starts and removes the Compose stack automatically. The remaining tests run in process.

## The projects

| Project | What it's for |
|---|---|
| `Portia.Abstractions` | The public contracts everything else implements — `Aggregate`, `DomainEvent`, `IRequest`/`IRequestHandler`/`IRequestBus`, `Result`, the transport marker interfaces (`ICallable`/`IQueuable`/`INotifiable`/`ISchedulable`), permission/authorization interfaces, `PortiaTelemetry`. |
| `Portia.Core` | The runtime pieces built on those contracts: `AggregateRepository`, `RequestBus`, `QueueRunner`, `RequestNotificationRunner`, `ProjectorRunner`/`ReactorRunner`, `MultiTenantRunner`, `EventSourcedTenantDirectory`. |
| `Portia.Generators` | The Roslyn source generators — DI registration, RPC worker registration, HTTP binding interceptors, the domain-event catalog, and the analyzers backing them (`PORTIA0xx` diagnostics). |
| `Portia.AspNetCore` | `MapPortiaGet`/`Post`/`Put`/`Patch`/`Delete`/`GetStream`/`GetSse` — the minimal-API extension methods the HTTP binding generator intercepts. |
| `Portia.Fitz` | Fitz-backed transports: RPC send/receive, queue publish/consume, notice/schedule notifications, `FitzEventStore`, and `FleetPartitionRunner` (fleet distribution via Fitz leases). |
| `Portia.Jwt` | A JWT-backed `IRequestActorValidator` — re-validates a request's carried actor token, no ASP.NET Core dependency. |
| `Portia.DependencyInjection` | Composes the application with fluent `AddPortia()` and activates its workers with `AddWorkers()`, which runs every declared projector and reactor under one hosted service. |
| `Portia.Testing` | Testing utilities for downstream apps: aggregate scenarios, in-memory stores, actor/permission doubles, and backend-neutral projection, fencing, and reaction-deduplication conformance suites. Fitz-specific doubles (`InMemoryRpcClient`, `InMemoryLeaseClient`) ship from `Portia.Fitz` instead, since they depend on it. |

## Core concepts, briefly

- **Event sourcing**: `Aggregate` with explicit `On<TEvent>(Action<TEvent> handler)` registration
  in the constructor — no source generator, no naming convention, a mismatched signature is an
  ordinary compile error. A stale `AppendAsync` (someone else committed to the stream first)
  throws `EventStreamConcurrencyException` from every `IEventStore` implementation — one stable
  type to catch. Portia never reruns the business command automatically.
  Inject `IAggregateRepository`, construct normally, and call
  `HydrateAsync(new Account(id))` followed by `SaveAsync(account, context, ct)`.
  [Request and reaction context](docs/request-context.md) supplies causal and actor attribution. Rehydrating the same
  instance reads only events after its committed position; no aggregate registration is needed. Raised events use the aggregate stream; audits use a fresh UUIDv4 session
  stream per batch in the same realm/area and leave aggregate OCC unchanged.
- **CQRS dispatch**: `Result`/`Result<T>` instead of exceptions for expected failures; a request
  opts into each transport by implementing that transport's marker interface, checked at compile
  time.
- **Permissions**: `[RequiresPermission("orders:{OrderId}:read")]` — the `{Token}` interpolates
  against the request's own primary-constructor properties, resolved and validated at compile
  time (`PORTIA011` catches an unknown token, `PORTIA013` catches a nullable one — a null value
  at dispatch time would otherwise silently collapse to an empty segment in the checked
  permission string instead of failing clearly). `IPermissionEvaluator` (coarse) and
  `IRequestAuthorizer<TScope>` policies are independent, pluggable hooks. A policy can target one
  request, a request-family interface, or every request; all matching policies run by semantic
  authorization stage after the coarse permission check and before the handler.
- **Actor propagation**: never ambient. Every `IRequestBus` call takes an explicit
  `ClaimsPrincipal`; queued and notice transports carry a raw JWT instead and re-validate it
  (signature and expiry) at the moment the request actually runs, not when it was submitted.
  `JwtRequestActorValidator` forces `TokenValidationParameters.ClockSkew` to zero on its own
  clone of whatever's passed in, regardless of the caller's own setting — left to
  `Microsoft.IdentityModel`'s five-minute default (which most JWT setup guides never mention
  overriding), a token that expired minutes ago would otherwise still validate successfully,
  found during adversarial review.
  Durable schedules are deliberately different: they accept only an explicit Portia system
  identity, persist its subject and issuer rather than a bearer token, and reconstruct that
  identity on every firing. User and anonymous principals are rejected before the schedule is
  written, so recurring work cannot expire with or retain the creator's JWT.
  This changes the durable schedule envelope. Before upgrading schedule workers, cancel or drain
  every schedule created by an older Portia version and recreate it with `RequestActor.System`
  (or a named `RequestActor.CreateSystem(...)` identity). A legacy firing fails with
  `LegacyScheduledRequestException`; Portia will not revive and execute its persisted bearer token.
- **Schema evolution**: `DomainEventTypeCatalog` maps a logical event name + schema version to a
  CLR type. An exact match resolves directly (old and new versions can simply coexist forever);
  a missing version falls through a chain of JSON-adapter-specific
  `IJsonDomainEventUpcaster`s. The generator contributes referenced domain-event types to the
  application catalog at compile time; typed request dispatch is inferred without reflection.
  Startup validates the actual JSON upcasters resolved from DI, including identities, duplicates,
  and every declared prospective transition. An upcaster is checked beginning at its next version
  even when its source CLR type remains registered, while exact historical catalog versions still
  deserialize directly. Replacing `IDomainEventSerializer` opts out of this JSON-specific policy.
  This validates compiled declarations only: Portia cannot prove that storage contains no older,
  undeclared schema version and does not audit the event store.
- **Multi-tenancy vs. fleet distribution — deliberately orthogonal**: `MultiTenantRunner`
  decides which tenants a component instance runs for, on whichever worker it's already on.
  `FleetPartitionRunner` decides which worker gets to run a given partition at all, using Fitz
  renewable membership leases and deterministic rendezvous hashing. Workers periodically
  reconcile an authoritative inventory, relinquish revoked assignments, and compete only for
  assigned partition leases. Membership uses a dedicated `lease://realm/area/*` selector.
  All workers must share the selector, partition set, and assignment algorithm. Hashing minimizes
  movement but does not guarantee equal partition counts. Downstream writes must enforce fencing
  against expired holders.
  Hosted workloads implement `ITenantWorkload` or `IPartitionWorkload`; each active tenant or
  held lease receives its own dependency-injection scope, which is disposed when that run stops.
- **Observability**: `PortiaTelemetry.ActivitySource` (`"Cntryl.Portia"`) traces every dispatch,
  wired once at the bus. Every background runner (`QueueRunner`, `RequestNotificationRunner`,
  `MultiTenantRunner`, `FleetPartitionRunner`) also accepts an optional `ILogger<TSelf>` —
  supply one directly, or configure `Microsoft.Extensions.Logging` with at least one provider
  before DI constructs the runner. A bare `ServiceCollection` registration does not provide a
  logger. If neither an activity listener nor a configured logger is present, no runner-fault
  signal is emitted.
- **Hosting**: a runner's `RunAsync` is never called automatically just by constructing it —
  `Portia.DependencyInjection` (and `Portia.Fitz`, for fleet) provides `IHostedService` wrappers
  (`AddWorkers()`, `AddPortiaQueueRunner()`, etc.) that start when the host
  starts and stop cleanly on shutdown. A projector loads the authoritative checkpoint from its
  constructor-injected `IProjectionStore`; each `IProjectionBatch` commits that checkpoint atomically with projection
  changes. Reactors, whose effects cannot share that transaction, use an
  `IProjectionCheckpointStore` — `InMemoryProjectionCheckpointStore` (`Portia.Testing`) for tests
  or a single-instance deployment; anything durable needs its own implementation. Both ports use
  `CheckpointIdentity(componentName, pattern, rebuildId)`; persist its canonical `Pattern` and
  nullable `RebuildId` alongside the component name. `ProjectionRunOptions.RebuildId` selects
  separate data and progress: reuse it to resume, choose a new ID to start at zero. Applications
  own rebuilt-data promotion once a rebuild is verified.

For trimming and NativeAOT setup, see the [NativeAOT guide](docs/native-aot.md).

## Scope

See the authoritative [scope](docs/scope.md) for what Portia does and does not support.
The [design decisions](docs/design-decisions.md) explain the failure modes and invariants behind
the framework's less-obvious boundaries.

[Projectors and reactors](docs/projectors-and-reactors.md) shows constructor-injected
repositories, batch handling, and storage contracts.

# Portia

Write a command and its handler:

`CreateGreeting.cs`:

```csharp
using Cntryl.Portia;

public sealed record CreateGreeting(string Name) : IRequest<string>, ICallable;

sealed class CreateGreetingHandler : IRequestHandler<CreateGreeting, string>
{
    public ValueTask<Result<string>> HandleAsync(
        IRequestContext<CreateGreeting> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success($"Hello, {context.Request.Name}!"));
}
```

Wire the command to an HTTP route:

`Program.cs`:

```csharp
using Cntryl.Portia;

var builder = WebApplication.CreateBuilder(args);
_ = builder.Services.AddPortiaGeneratedComponents();

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

## Why it works

- `IRequest<string>` says that `CreateGreeting` returns text.
- `ICallable` says that the application may expose it to callers, including over HTTP.
- `IRequestHandler<CreateGreeting, string>` connects that command to its handler.
- `AddPortiaGeneratedComponents()` adds the command and handler to the application.
- `MapPortiaPost` reads the HTTP request, calls the handler, and writes the HTTP response.

Portia writes the repetitive connection code when the project builds. It is ordinary C# checked
by the compiler—there is no runtime scanning, naming convention, controller, or registration list
to keep in sync. If the command and handler disagree about their types, the build fails.

## How we keep it simple

The handler only knows about the command. It does not know about HTTP, queues, or any other way
the command might arrive. That lets the same handler run without being copied or wrapped when the
application adds another way to call it.

Every command follows the same path to its handler. Permissions, identity checks, and tracing can
be added to that path once instead of being repeated in every endpoint. A command explicitly opts
into the ways it may be called, so behavior stays visible in its type rather than hidden in setup
code.

[Read the getting-started guide](docs/getting-started.md) for package setup, authorization,
asynchronous work, and streaming. Runnable applications will live in dedicated sample
repositories rather than in this framework repository.

## Development

To run the test suite:

```
docker compose up --detach fitz
dotnet test test/Portia.Tests/Portia.Tests.csproj
docker compose down --volumes
```

`FitzBrokerIntegrationTests` connects to the Compose-managed broker at
`ws://127.0.0.1:4090/ws` by default; override `FITZ_TEST_ENDPOINT` when using another
broker. CI starts and removes the Compose stack automatically. Everything else needs nothing
running.

## The projects

| Project | What it's for |
|---|---|
| `Portia.Abstractions` | The public contracts everything else implements — `Aggregate`, `DomainEvent`, `IRequest`/`IRequestHandler`/`IRequestBus`, `Result`, the transport marker interfaces (`ICallable`/`IQueuable`/`INotifiable`/`ISchedulable`), permission/authorization interfaces, `PortiaTelemetry`. |
| `Portia.Core` | The runtime pieces built on those contracts: `QueueRunner`, `LiveRequestRunner`, `ProjectorRunner`/`ReactorRunner`, `MultiTenantRunner`, `EventSourcedTenantDirectory`. |
| `Portia.Generators` | The Roslyn source generators — DI registration, RPC worker registration, HTTP binding interceptors, the domain-event catalog, and the analyzers backing them (`PORTIA0xx` diagnostics). |
| `Portia.AspNetCore` | `MapPortiaGet`/`Post`/`Put`/`Patch`/`Delete`/`GetStream`/`GetSse` — the minimal-API extension methods the HTTP binding generator intercepts. |
| `Portia.Fitz` | Fitz-backed transports: RPC send/receive, queue publish/consume, notice/schedule live delivery, `FitzEventStore`, and `FleetPartitionRunner` (fleet distribution via Fitz leases). |
| `Portia.Jwt` | A JWT-backed `IRequestActorValidator` — re-validates a request's carried actor token, no ASP.NET Core dependency. |
| `Portia.DependencyInjection` | Wires Portia's background runners into a host as `IHostedService`s — `AddPortiaQueueRunner()`, `AddPortiaLiveRequestRunner()`, `AddPortiaMultiTenantRunner()`, `AddPortiaProjectorRunner<T>()`, `AddPortiaReactorRunner<T>()`. Fleet's `AddPortiaFleetPartitionRunner()` lives in `Portia.Fitz` instead, since it depends on Fitz leases. |
| `Portia.Testing` | Testing utilities for downstream apps: `InMemoryEventStore`, `TestPermissionEvaluator`, `TestRequestActorValidator`. Fitz-specific doubles (`InMemoryRpcClient`, `InMemoryLeaseClient`) ship from `Portia.Fitz` instead, since they depend on it. |

## Core concepts, briefly

- **Event sourcing**: `Aggregate` with explicit `On<TEvent>(Action<TEvent> handler)` registration
  in the constructor — no source generator, no naming convention, a mismatched signature is an
  ordinary compile error. A stale `AppendAsync` (someone else committed to the stream first)
  throws `EventStreamConcurrencyException` from every `IEventStore` implementation — one stable
  type to catch and retry against, confirmed against a real Fitz broker, not just
  `InMemoryEventStore`'s own in-process check.
- **CQRS dispatch**: `Result`/`Result<T>` instead of exceptions for expected failures; a request
  opts into each transport by implementing that transport's marker interface, checked at compile
  time.
- **Permissions**: `[RequiresPermission("orders:{OrderId}:read")]` — the `{Token}` interpolates
  against the request's own primary-constructor properties, resolved and validated at compile
  time (`PORTIA011` catches an unknown token, `PORTIA013` catches a nullable one — a null value
  at dispatch time would otherwise silently collapse to an empty segment in the checked
  permission string instead of failing clearly). `IPermissionEvaluator` (coarse) and
  `IRequestAuthorizer<T>` (row-level) are independent, pluggable hooks — permission is always
  checked before the authorizer.
- **Actor propagation**: never ambient. Every `IRequestBus` call takes an explicit
  `ClaimsPrincipal`; queued/scheduled transports carry a raw JWT instead and re-validate it
  (signature and expiry) at the moment the request actually runs, not when it was enqueued.
  `JwtRequestActorValidator` forces `TokenValidationParameters.ClockSkew` to zero on its own
  clone of whatever's passed in, regardless of the caller's own setting — left to
  `Microsoft.IdentityModel`'s five-minute default (which most JWT setup guides never mention
  overriding), a token that expired minutes ago would otherwise still validate successfully,
  found during adversarial review.
- **Schema evolution**: `DomainEventTypeCatalog` maps a logical event name + schema version to a
  CLR type. An exact match resolves directly (old and new versions can simply coexist forever);
  a missing version falls through a chain of `IDomainEventUpcaster`s. Populated automatically by
  `DomainEventCatalogGenerator` from every `DomainEvent` type in the compilation.
- **Multi-tenancy vs. fleet distribution — deliberately orthogonal**: `MultiTenantRunner`
  decides which tenants a component instance runs for, on whichever worker it's already on.
  `FleetPartitionRunner` decides which worker gets to run a given partition at all, using Fitz
  leases for contention — rebalancing needs no explicit logic; it falls out of independent,
  per-partition lease contention.
- **Observability**: `PortiaTelemetry.ActivitySource` (`"Cntryl.Portia"`) traces every dispatch,
  wired once at the bus. Every background runner (`QueueRunner`, `LiveRequestRunner`,
  `MultiTenantRunner`, `FleetPartitionRunner`) also accepts an optional `ILogger<TSelf>` —
  supply one directly, or configure `Microsoft.Extensions.Logging` with at least one provider
  before DI constructs the runner. A bare `ServiceCollection` registration does not provide a
  logger. If neither an activity listener nor a configured logger is present, no runner-fault
  signal is emitted.
- **Hosting**: a runner's `RunAsync` is never called automatically just by constructing it —
  `Portia.DependencyInjection` (and `Portia.Fitz`, for fleet) provides `IHostedService` wrappers
  (`AddPortiaQueueRunner()`, `AddPortiaProjectorRunner<T>()`, etc.) that start when the host
  starts and stop cleanly on shutdown. `ProjectorRunner`/`ReactorRunner` are batch-pass methods,
  not run-forever loops, so their hosted wrappers also need an `IProjectionCheckpointStore` —
  `InMemoryProjectionCheckpointStore` (`Portia.Testing`) for tests or a single-instance
  deployment; anything durable needs its own implementation.

## Known gaps, stated plainly

- **No aggregate snapshotting.** `Aggregate.Load` replays the full committed-event history every
  time; there's no checkpoint mechanism yet. Fine at low event counts, a real scaling concern
  for anything long-lived.
- **`ReactorRunner`'s bounded, checkpointed batching is opt-in, not automatic.** Found during
  adversarial review: a reactor's job is raising commands against other aggregates, not generally
  safe to redo, but a failure partway through an unbounded pass used to return no checkpoint at
  all. Fixed — pass an `IProjectionCheckpointStore` and a `maxBatchSize` to `RunAsync` (as
  `AddPortiaReactorRunner<T>()` already does) and a failure only loses the current batch, not the
  whole pass. Omit them and you keep the older, whole-pass-only behavior; that's a deliberate
  choice this class leaves to the caller.

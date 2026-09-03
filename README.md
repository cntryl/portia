# Portia

Event sourcing and CQRS for .NET, built around one bet: **a handler, written once, runs
unmodified regardless of how the request arrived** — HTTP, Fitz RPC, a queue, a fired schedule
entry, live notice fanout, or a direct in-process call. Every transport dispatches through the
same generated `IRequestBus`, so permissions, row-level authorization, actor/JWT validation, and
OpenTelemetry tracing are wired once, at that one chokepoint, and apply to every transport for
free — never per-transport, never something a handler has to know about.

Zero-boilerplate is enforced by construction, not convention: DI registration, RPC worker
registration, HTTP request binding, and domain-event-catalog population are all generated from
compile-time discovery (Roslyn source generators), not runtime reflection or assembly scanning.
The two deliberate exceptions are wire boundaries (`JsonRequestSerializer`,
`JsonDomainEventSerializer`) where a byte payload has no way around naming its own type.

## Quickstart

The fastest way to see the shape of an app built on Portia is to read
[`samples/Portia.Samples.WebApi/Program.cs`](samples/Portia.Samples.WebApi/Program.cs) — it's
short, heavily commented, and runs standalone:

```
dotnet run --project samples/Portia.Samples.WebApi
```

It demonstrates, in order: a guarded command (`[RequiresPermission]`), the `Prefer:
respond-async` queue pivot (no separate "Async"-suffixed endpoint), route/query/body binding
with no attributes and no ASP.NET Core reference on the request type itself, and both streaming
shapes (incremental JSON array, Server-Sent Events) off the same handler.

To run the test suite:

```
docker compose up --detach fitz
dotnet test test/Portia.Tests/Portia.Tests.csproj
docker compose down --volumes
```

`FitzBrokerIntegrationTests` connects to the Compose-managed broker at
`ws://127.0.0.1:4090/ws` by default; override `PORTIA_FITZ_TEST_ENDPOINT` when using another
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
| `Portia.DependencyInjection` | `Microsoft.Extensions.DependencyInjection` integration for reactors and projectors. |
| `Portia.Testing` | Testing utilities for downstream apps: `InMemoryEventStore`, `TestPermissionEvaluator`, `TestRequestActorValidator`. Fitz-specific doubles (`InMemoryRpcClient`, `InMemoryLeaseClient`) ship from `Portia.Fitz` instead, since they depend on it. |

## Core concepts, briefly

- **Event sourcing**: `Aggregate` with explicit `On<TEvent>(Action<TEvent> handler)` registration
  in the constructor — no source generator, no naming convention, a mismatched signature is an
  ordinary compile error.
- **CQRS dispatch**: `Result`/`Result<T>` instead of exceptions for expected failures; a request
  opts into each transport by implementing that transport's marker interface, checked at compile
  time.
- **Permissions**: `[RequiresPermission("orders:{OrderId}:read")]` — the `{Token}` interpolates
  against the request's own primary-constructor properties, resolved and validated at compile
  time (`PORTIA011` catches an unknown token). `IPermissionEvaluator` (coarse) and
  `IRequestAuthorizer<T>` (row-level) are independent, pluggable hooks — permission is always
  checked before the authorizer.
- **Actor propagation**: never ambient. Every `IRequestBus` call takes an explicit
  `ClaimsPrincipal`; queued/scheduled transports carry a raw JWT instead and re-validate it
  (signature and expiry) at the moment the request actually runs, not when it was enqueued.
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

## Known gaps, stated plainly

- **No aggregate snapshotting.** `Aggregate.Load` replays the full committed-event history every
  time; there's no checkpoint mechanism yet. Fine at low event counts, a real scaling concern
  for anything long-lived.
- **The sample app is CQRS-only.** No event-sourced aggregate, multi-tenancy, or fleet
  distribution example exists yet, despite all three being implemented and tested.

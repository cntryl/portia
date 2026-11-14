# Portia

Portia is a cohesive application framework for .NET 11. It carries an operation from its entry point through
authorization and domain decisions, then runs the durable work that follows.

ASP.NET Core already does HTTP well. Portia is the application model behind HTTP—and behind RPC, queues, notices,
schedules, and local calls. A request keeps the same handler, actor, authorization, causation, result, and telemetry
semantics regardless of how it arrives.

```text
HTTP / RPC / queue / schedule / local
                    │
                    ▼
 request → authorization → behaviors → guards → handler → aggregate → Fitz event log
                                                           │
                                           ┌───────────────┴───────────────┐
                                           ▼                               ▼
                                projector + checkpoint              reactor + effect
                                           │
                                           ▼
                           Cassie / PostgreSQL / Snowflake / ...
```

The unit of value is not `command → handler → route`. It is the complete path from intent to durable consequence.

## What Portia provides

- **One application operation, many entry points.** A handler is selected once and can be reached locally or through an
  explicitly allowed transport without transport logic entering the handler.
- **One execution context.** Actor identity, authorization, correlation, causation, expected failures, tracing, and
  metrics follow the request across process and delivery boundaries.
- **Event-sourced decisions.** Aggregates are ordinary application objects. Portia hydrates and saves their events with
  optimistic concurrency and stable event identities.
- **Durable consequences.** Projectors atomically commit read-model changes with their checkpoints. Reactors run
  external effects at least once with preserved event causation. Failed work never silently advances progress.
- **Inspectable processors.** Each hosted projector and reactor exposes one operator-only [status RPC](docs/projectors-and-reactors.md#operator-inspection-of-a-projector-or-reactor)
  for committed progress, source backlog, and available failure evidence.
- **One composition, independent deployments.** An API and its workers share application setup but can be deployed and
  scaled separately. Workers support global, per-tenant, and fleet-coordinated ownership.
- **Compile-time wiring.** Registration, transport descriptors, HTTP binding, and JSON roots are source-generated and
  checked by Portia analyzers. The supported runtime path uses no assembly scanning or reflection, and NativeAOT smoke
  consumers under `smoke/` exercise it.
- **Reusable request preflight.** Scoped asynchronous guards run at the unary handler boundary, can target one request
  or an application-owned request family, and short-circuit with the ordinary `Result` contract.

## A vertical slice

A request declares its stable identity and the transports the application permits:

```csharp
[RequestRoute("banking", "accounts", "*", "deposit")]
[Discriminator("accounts.deposit")]
public sealed record Deposit(Uuid AccountId, int Amount)
    : IRequest, ICallable, IQueuable;
```

Its handler contains the application decision, not HTTP or queue plumbing:

```csharp
public sealed class DepositHandler(IAggregateExecutor aggregates)
    : IRequestHandler<Deposit>
{
    public ValueTask<Result> HandleAsync(
        IRequestContext<Deposit> context,
        CancellationToken ct) =>
        aggregates.ExecuteAsync(
            new Account(context.Request.AccountId),
            account => AggregateOutcome.Commit(account.Deposit(context.Request.Amount)),
            context,
            ct);
}
```

The application registers the behavior and the durable work caused by its events:

```csharp
services.AddPortia()
    .AddRequestHandler<DepositHandler>()
    .AddProjector<AccountBalanceProjector>("AccountBalanceProjector", WorkloadScope.PerTenant)
    .AddReactor<DepositReceiptReactor>("DepositReceiptReactor", WorkloadScope.PerTenant)
    .AddFitz(configuration.GetSection("Fitz"));
```

Per-tenant projectors and reactors declare their source with
`EventStreamPattern.ForTenant(area, resource)`; Portia binds the actual tenant realm before reading. Global workloads
use an exact `EventStreamPattern.ForPattern(realm, area, resource)`.

An API host explicitly maps HTTP endpoints. A worker host calls the same application setup and adds `.AddWorkers()`. The
handler and domain model do not change when the operation is sent over RPC, placed on a queue, or invoked in-process.

The
[complete consumer fixture](test/Portia.ConsumerTests/Requests/CompleteWorkflowTests.cs)
executes one business handler through direct dispatch, HTTP, RPC, and a real Fitz queue, then proves that the resulting
events retain actor and causal metadata and drive projectors and reactors.

## Persistence without a lowest-common-denominator model

Fitz is Portia's first-party event log, transport, scheduler, and coordination layer. Cassie is the first-party
read-model engine for SQL, graph, time-series, and vector workloads.

Neither is hidden behind a generic query language. A projector receives an ordinary application repository built on the
backend's native client and schema. Read models are simple data contracts returned by repository
queries; repositories own persistence behavior, and projectors apply events through them. That repository implements
`IProjectionStore` and returns `IProjectionBatch` handles to give Portia the lifecycle and atomic read-model-plus-checkpoint
boundary it needs.

PostgreSQL, Snowflake, or another store can therefore be added in userland without changing Portia, writing a custom
runner, or translating queries into a framework DSL. If a target cannot atomically commit its changes and checkpoint, it
is modeled honestly as an at-least-once reactor effect instead.

## Choose the right Fitz primitive

| Use | When |
| --- | --- |
| **Event streams** | Record ordered, authoritative aggregate decisions that must be rehydrated or replayed. Each append is atomic for one aggregate stream. |
| **KV** | Store keyed current or derived state, such as projection data and processor checkpoints. KV is not the aggregate event history. |
| **Queues** | Send work to one competing consumer, with optional initial delay, message TTL, and retry delay. Consumers must tolerate duplicates. Critical work needs durable intent and recovery because queue acceptance does not guarantee crash recovery. |
| **Notices** | Fan out notifications to subscribers, such as waking interested workers. Each replica receives the notice; it is not competing queue work. |
| **Schedules** | Trigger work by time or recurrence. Choose the provider's delivery policy deliberately; a received Fitz firing can be lost on process failure. |

See [application setup](docs/application-setup.md) and [Fitz upgrade notes](docs/fitz-4-upgrade.md) for transport and durability details.

## Start here

- [Getting started](docs/getting-started.md): packages, contracts, registration, HTTP, and transports
- [Shared application setup](docs/application-setup.md): API and worker deployments from one composition
- [Projectors and reactors](docs/projectors-and-reactors.md): native repositories and durable processing
- [Processor operations](docs/operations.md): durable diagnosis, operator approval, worker-owned recovery, and verification
- [Request context](docs/request-context.md): actor, correlation, and causation
- [Request identities](docs/request-identities.md): typed logical request IDs with `IIdentifiedRequest`
- [Validation authority](docs/validation-authority.md): local invariants, advisory reads, and authoritative outcome choreography.
- [Request guards](docs/request-guards.md): authorization, reusable asynchronous preflight, and authoritative invariants
- [Testing applications](docs/testing.md): aggregate, request, projector, and reactor scenarios
- [Object storage](docs/object-storage.md): vendor-neutral contracts and tenant-scoped AWS, Azure and Google Cloud adapters
- [Outbound mail and SMS](docs/communications.md): provider-neutral senders with SES, Azure and Twilio providers
- [Cassie projections](docs/cassie-projections.md): native SQL repositories, typed projectors, and atomic checkpoints.
- [Platform vision](docs/platform-vision.md): the fixed Fitz–Portia–Cassie boundary
- [Scope](docs/scope.md) and [design decisions](docs/design-decisions.md): guarantees and deliberate limits
- [Observability](docs/observability.md): traces, metrics, structured logs, and Prometheus dashboards
- [Performance and scaling](docs/performance-and-scaling.md): measured hot paths and scaling model
- [NativeAOT](docs/native-aot.md): trimming and source-generated JSON setup
- [Model Context Protocol](docs/mcp.md): request-backed tools, resources, and prompts over stdio and Streamable HTTP
- [Portia 0.11.1 migration notes](docs/migration-0.11.1.md): stateless MCP HTTP, object storage completion outcomes, and Abstractions references
- [Portia 0.11.0 migration notes](docs/migration-0.11.0.md): .NET 11, Fitz 4.1, JSON, and compiler updates
- [Portia 0.10.0 migration notes](docs/migration-0.10.0.md): operator access, processor inspection, typed outcomes, and Fitz 4
- [Portia 0.8 migration notes](docs/migration-0.8.md): Fitz 2.0 compatibility and queue persistence
- [Portia 0.7 migration notes](docs/migration-0.7.md): request-scenario cancellation and bounded manual processor passes
- [Migrating to Portia 0.4](docs/migration-0.4.md): JSON ownership, tenant templates, and MCP tests
- [Fitz 4 upgrade](docs/fitz-4-upgrade.md): Portia 0.11.0's Wire 3 compatibility, retry policy, and persistence

## Development

Package versions are managed in `Directory.Packages.props`; restore the solution normally.
The standalone examples and package-consumer smoke projects declare their external versions
explicitly and select the Portia package version during qualification. Install a stable .NET 11 SDK;
`global.json` sets 11.0.100 as the minimum SDK and rolls forward to the latest installed .NET 11
feature band.

```sh
dotnet restore Portia.slnx
dotnet format Portia.slnx --verify-no-changes --no-restore
dotnet build Portia.slnx --configuration Release --no-restore
dotnet test Portia.slnx --configuration Release --no-build --filter "Category!=BrokerIntegration&Category!=StorageIntegration&Category!=CassieIntegration&Category!=DurabilityIntegration"
docker compose up --detach --wait fitz
dotnet test Portia.slnx --configuration Release --no-build --filter "Category=BrokerIntegration"
docker compose up --detach sqrzl
curl --fail --silent --show-error --retry 30 --retry-delay 1 --retry-connrefused --max-time 2 --output /dev/null http://127.0.0.1:9000/healthz
PORTIA_S3_TEST_ENDPOINT=http://127.0.0.1:9000 dotnet test Portia.slnx --configuration Release --no-build --filter "Category=StorageIntegration"
docker compose down --volumes
```

Broker integration uses `ws://127.0.0.1:4090/ws` by default. Override it with
`FITZ_TEST_ENDPOINT`. Storage integration requires the Sqrzl S3 endpoint supplied by
`PORTIA_S3_TEST_ENDPOINT`. CI runs restore, formatting, Release build, service-free tests, and the
broker, object-storage, Cassie, and crash-durability integration categories.
The first test command needs no external service.

## License

Portia is licensed under the [Apache License, Version 2.0](LICENSE).

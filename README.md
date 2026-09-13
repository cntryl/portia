# Portia

Portia is a cohesive application framework for .NET 10. It carries an operation from its entry
point through authorization and domain decisions, then runs the durable work that follows.

ASP.NET Core already does HTTP well. Portia is the application model behind HTTP—and behind RPC,
queues, notices, schedules, and local calls. A request keeps the same handler, actor, authorization,
causation, result, and telemetry semantics regardless of how it arrives.

```text
HTTP / RPC / queue / schedule / local
                    │
                    ▼
 request → authorization → behaviors → handler → aggregate → Fitz event log
                                                           │
                                           ┌───────────────┴───────────────┐
                                           ▼                               ▼
                                projector + checkpoint              reactor + effect
                                           │
                                           ▼
                           Cassie / PostgreSQL / Snowflake / ...
```

The unit of value is not `command → handler → route`. It is the complete path from intent to
durable consequence.

## What Portia provides

- **One application operation, many entry points.** A handler is selected once and can be reached
  locally or through an explicitly allowed transport without transport logic entering the handler.
- **One execution context.** Actor identity, authorization, correlation, causation, expected
  failures, tracing, and metrics follow the request across process and delivery boundaries.
- **Event-sourced decisions.** Aggregates are ordinary application objects. Portia hydrates and
  saves their events with optimistic concurrency and stable event identities.
- **Durable consequences.** Projectors atomically commit read-model changes with their checkpoints.
  Reactors run external effects at least once with preserved event causation. Failed work never
  silently advances progress.
- **One composition, independent deployments.** An API and its workers share application setup but
  can be deployed and scaled separately. Workers support global, per-tenant, and fleet-coordinated
  ownership.
- **Compile-time wiring.** Registration, transport descriptors, HTTP binding, and JSON roots are
  source-generated and checked by Portia analyzers. The supported runtime path uses no assembly
  scanning or reflection and is exercised as a packed NativeAOT application in CI.

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
public sealed class DepositHandler(IAggregateRepository aggregates)
    : IRequestHandler<Deposit>
{
    public async ValueTask<Result> HandleAsync(
        IRequestContext<Deposit> context,
        CancellationToken ct)
    {
        var account = await aggregates.HydrateAsync(
            new Account(context.Request.AccountId), ct);

        account.Deposit(context.Request.Amount);
        await aggregates.SaveAsync(account, context, ct);
        return Result.Success;
    }
}
```

The application registers the behavior and the durable work caused by its events:

```csharp
services.AddPortia()
    .AddRequestHandler<DepositHandler>()
    .AddProjector<AccountBalanceProjector>(WorkloadScope.PerTenant)
    .AddReactor<DepositReceiptReactor>(WorkloadScope.PerTenant)
    .AddFitz(configuration.GetSection("Fitz"));
```

An API host explicitly maps HTTP endpoints. A worker host calls the same application setup and
adds `.AddWorkers()`. The handler and domain model do not change when the operation is sent over
RPC, placed on a queue, or invoked in-process.

The
[complete consumer fixture](test/Portia.ConsumerTests/CompleteWorkflowTests.cs)
executes one business handler through direct dispatch, HTTP, RPC, and a real Fitz queue, then proves
that the resulting events retain actor and causal metadata and drive projectors and reactors.

## Persistence without a lowest-common-denominator model

Fitz is Portia's first-party event log, transport, scheduler, and coordination layer. Cassie is the
first-party read-model engine for SQL, graph, time-series, and vector workloads.

Neither is hidden behind a generic query language. A projector receives an ordinary application
repository built on the backend's native client and schema. That repository implements
`IProjectionStore` and `IProjectionBatch` only to give Portia the lifecycle and atomic
read-model-plus-checkpoint boundary it needs.

PostgreSQL, Snowflake, or another store can therefore be added in userland without changing
Portia, writing a custom runner, or translating queries into a framework DSL. If a target cannot
atomically commit its changes and checkpoint, it is modeled honestly as an at-least-once reactor
effect instead.

## Start here

- [Getting started](docs/getting-started.md): packages, contracts, registration, HTTP, and transports
- [Shared application setup](docs/application-setup.md): API and worker deployments from one composition
- [Projectors and reactors](docs/projectors-and-reactors.md): native repositories and durable processing
- [Request context](docs/request-context.md): actor, correlation, and causation
- [Platform vision](docs/platform-vision.md): the fixed Fitz–Portia–Cassie boundary
- [Scope](docs/scope.md) and [design decisions](docs/design-decisions.md): guarantees and deliberate limits
- [Performance and scaling](docs/performance-and-scaling.md): measured hot paths and scaling model
- [NativeAOT](docs/native-aot.md): trimming and source-generated JSON setup

## Development

```sh
dotnet format Portia.slnx --verify-no-changes
dotnet build Portia.slnx --configuration Release
dotnet test Portia.slnx --configuration Release --no-build --filter "Category!=BrokerIntegration"
docker compose up --detach --wait fitz
dotnet test Portia.slnx --configuration Release --no-build --filter "Category=BrokerIntegration"
docker compose down --volumes
```

Broker integration uses `ws://127.0.0.1:4090/ws` by default. Override it with
`FITZ_TEST_ENDPOINT`. CI runs the same format, build, broker-free test, packed-consumer, and broker
integration sequence.

## License

Portia is licensed under the [Apache License, Version 2.0](LICENSE).

# Projectors and reactors

Persistence is an ordinary constructor dependency. The same repository implements
application operations and the contract Portia needs to manage progress.

| Base | Handling | Progress |
|---|---|---|
| `BaseProjector` | One event | Atomic projection-data/checkpoint commit per event |
| `BaseBatchProjector` | Bounded batch | Atomic projection-data/checkpoint commit per batch |
| `BaseReactor` | One event | Checkpoint after the reaction succeeds |
| `BaseBatchReactor` | Bounded batch | Checkpoint after all reactions in the batch succeed |

## Constructor-injected repository

```csharp
public interface IAccountRepository : IProjectionStore
{
    ValueTask IncrementBalanceAsync(Uuid accountId, int amount, CancellationToken ct);
}

public sealed partial class AccountProjector(IAccountRepository accounts)
    : BaseBatchProjector(accounts, EventStreamPattern.ForPattern("accounts", "balances")),
      IProjectorHandler<MoneyDeposited>
{
    public ValueTask HandleAsync(
        MoneyDeposited ev, IProjectorContext context, CancellationToken ct)
        => accounts.IncrementBalanceAsync(ev.Metadata.AggregateId, ev.Amount, ct);
}
```

```csharp
services.AddScoped<IAccountRepository, AccountRepository>();
services.AddPortia()
    .AddProjector<AccountProjector>(WorkloadScope.PerTenant);
```

`Portia.DependencyInjection` supplies the generator for typed dispatch.
`IProjectorContext` contains checkpoint identity and rebuild metadata, never application
services. `WorkloadScope.PerTenant` replaces the declared pattern's realm with the active tenant ID;
`WorkloadScope.Global` keeps the declared realm. Names default to the concrete type's full name;
use a constructor name for a manually run component or registration `options.Name`
for an explicitly named hosted workload.

`IProjectionStore` exposes `LoadCheckpointAsync(identity, ct)` and
`BeginAsync(ProjectionBatchContext, ct)`. The latter returns `IProjectionBatch`, a lifecycle
handle with `CommitAsync(checkpoint, ct)` and `DisposeAsync()`. The repository itself
receives every application write during that unit of work. No extra projection-port
interface or target adapter is required.

Commit must atomically persist both changes and progress. Disposal releases resources
and discards uncommitted changes; it never commits. `ProjectionBatchContext.Checkpoint`
provides the expected starting progress for conditional writes. Use the complete
`CheckpointIdentity` to separate tenants, components, and rebuild generations. Scoped
repositories may inject `WorkloadContext` to inspect the current workload identity.

## Bulk handling

A batch base accepts ordinary per-event handlers, applying them in source order inside
one unit of work. To issue bulk application operations, implement a batch handler:

```csharp
public sealed partial class AccountProjector(IAccountRepository accounts)
    : BaseBatchProjector(accounts, EventStreamPattern.ForPattern("accounts", "balances")),
      IBatchProjectorHandler<MoneyDeposited>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<MoneyDeposited> events, IProjectorContext context, CancellationToken ct)
    {
        foreach (var ev in events)
            await accounts.IncrementBalanceAsync(ev.Metadata.AggregateId, ev.Amount, ct);
        // A repository-specific bulk operation can replace this loop.
    }
}
```

Generated batch dispatch delivers contiguous groups for the selected event handler.
It preserves source order across event types and selects more-derived handlers first;
it does not regroup events across intervening event types. All groups within a bounded
batch share one commit. Unhandled events still advance progress.

Set `options.Processing.MaxBatchSize` through a new `ProjectionRunOptions` instance to
bound a batch (default 512). This counts source events, not database write actions.
Single-event bases always commit progress per event. Batch handler interfaces require
a batch base, and one event type cannot select both handler modes (`PORTIA017`).

Manual implementations can override `ProjectEventAsync` / `ProjectBatchAsync`, or
`ReactToEventAsync` / `ReactBatchAsync`, instead of using generated typed handlers.

## Reactions

A reactor performs an application effect after observing an event. There are two ordinary
patterns; choose the one that describes the behavior rather than wrapping every effect in a
command mechanically.

Dispatch a command when the behavior is a reusable application operation, needs the request
authorization and handler pipeline, or can also originate outside the reactor:

```csharp
public sealed partial class AccountReactor(
    IProjectionCheckpointStore checkpoints,
    IRequestBus bus)
    : BaseReactor(checkpoints, EventStreamPattern.ForPattern("accounts", "balances")),
      IReactorHandler<MoneyDeposited>
{
    public ValueTask HandleAsync(IReactorContext<MoneyDeposited> context, CancellationToken ct) =>
        bus.SendReactionAsync(
            new SendDepositReceipt(context.Ev.Metadata.AggregateId), context, ct);
}
```

`SendReactionAsync` creates a child request from that event's reaction context, preserving its
system actor, correlation, and causation. A failed command result throws
`ReactionCommandFailedException`, so the reactor does not silently checkpoint a failed effect.
Result-bearing requests use `SendAsync<T>` directly because the reactor must decide what the
returned value and expected failures mean.

Call an injected integration service directly when the action is inherently caused by the event
and adding a request contract would add no useful application boundary:

```csharp
public interface IAccountReactions : IProjectionCheckpointStore
{
    ValueTask SendReceiptAsync(MoneyDeposited ev, IExecutionContext context, CancellationToken ct);
}

public sealed partial class AccountReactor(IAccountReactions accounts)
    : BaseBatchReactor(accounts, EventStreamPattern.ForPattern("accounts", "balances")),
      IBatchReactorHandler<MoneyDeposited>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<IReactorContext<MoneyDeposited>> contexts, CancellationToken ct)
    {
        foreach (var context in contexts)
            await accounts.SendReceiptAsync(context.Ev, context, ct);
    }
}
```

Register `IAccountReactions` through ordinary scoped DI and the reactor through
`portia.AddReactor<AccountReactor>(WorkloadScope.PerTenant)`. No separate framework checkpoint
registration is required when that application dependency implements the contract.

Every event retains its own system execution identity and causation. Do not use the
first event's context for the whole batch. Checkpoints advance only after processing
succeeds, but external effects can already have occurred when a later effect or checkpoint
write fails. Both command handlers reached from reactors and direct effects must tolerate replay;
batching is not an external transaction or an exactly-once guarantee. Use the triggering
`DomainEventMetadata.EventId` as the deduplication key when the target supports one.

## Storage implementations

The contract supports either a live transaction or buffered writes:

- **EF Core relational providers:** the repository's scoped `DbContext` uses one
  transaction for projection writes and checkpoint advancement. Multiple contexts must
  share the same connection and transaction. Dispose/reset failed work before reuse.
  See [EF Core transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions).
- **ADO.NET:** application commands and the checkpoint command use the same connection
  and `DbTransaction`. See [ADO.NET local transactions](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/local-transactions).
- **DynamoDB:** begin buffering, combine application writes and checkpoint advancement
  in `TransactWriteItems`, and discard the buffer on disposal. Coalesce changes to the
  same item; a transaction cannot contain multiple actions on one item. The transaction
  allows at most 100 actions and 4 MB, within one account and Region. Reserve capacity
  for checkpoint writes and enforce optimistic conditions for read-modify-write operations.
  See [TransactWriteItems](https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html).

These are implementation requirements, not bundled database adapters. The framework
cannot predict how many backend writes an event causes. Applications must select suitable
batch sizes and their repositories must enforce backend limits without partially committing
an oversized projection batch. Test atomicity, conditional conflicts, cancellation,
and ambiguous commit responses against the chosen backend.

`Portia.Testing` turns the storage requirements into executable suites:

- `ProjectionStoreConformance` verifies atomic data/checkpoint commits, rollback, optimistic
  conflicts, authoritative reloads, and rebuild isolation.
- `ReactionDeduplicationConformance` verifies an optional application deduplication primitive.
  It cannot prove crash atomicity between an external effect and its bookkeeping; use an
  idempotent sink or an integration-specific transactional inbox/outbox when that guarantee is
  required.

The suites accept small probe implementations and throw `ConformanceViolationException`, so an
application can invoke them from its normal test framework. Future official persistence adapters
must pass the applicable suites, but no database adapter is bundled today.

`Portia.Testing` supplies backend-neutral `ProjectionStoreConformance` and optional
`ReactionDeduplicationConformance` suites. Implement
their small probe interfaces in the application's storage test project and run `VerifyAsync` from
the test framework already in use. The deduplication suite proves duplicate suppression but cannot
prove crash atomicity between an external effect and bookkeeping; use an idempotent target or a
transactional inbox/outbox when that failure window matters.

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
services.AddPortia(p =>
{
    p.AddProjector<AccountProjector>(o => o.PerTenant());
});
```

Reference `Portia.Generators` in the component's project for typed dispatch.
`IProjectorContext` contains checkpoint identity and rebuild metadata, never application
services. `PerTenant()` replaces the declared pattern's realm with the active tenant ID;
`Global()` keeps the declared realm. Names default to the concrete type's full name;
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
repositories may inject `WorkloadContext` to enforce the current ownership fence.

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
`portia.AddReactor<AccountReactor>(o => o.PerTenant())`. No separate framework checkpoint
registration is required when that application dependency implements the contract.

Every event retains its own system execution identity and causation. Do not use the
first event's context for the whole batch. Checkpoints advance only after processing
succeeds, but external effects can already have occurred when a later effect or checkpoint
write fails. Both reactor bases require idempotent effects; batching is not an external
transaction or an exactly-once guarantee.

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
an oversized projection batch. Test atomicity, conditional conflicts, fencing, cancellation,
and ambiguous commit responses against the chosen backend.

[Verification evidence](processor-base-verification.md) records the red → green checks
and installed API/worker package proof.

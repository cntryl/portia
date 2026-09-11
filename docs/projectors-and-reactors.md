# Projectors and reactors

Persistence is an ordinary constructor dependency. The same repository implements
application operations and the contract Portia needs to manage progress.

| Base | Handling | Progress |
|---|---|---|
| `Projector` | One event | Atomic projection-data/checkpoint commit per event |
| `BatchProjector` | Bounded batch | Atomic projection-data/checkpoint commit per batch |
| `Reactor` | One event | Checkpoint after the reaction succeeds |
| `BatchReactor` | Bounded batch | Checkpoint after all reactions in the batch succeed |

## Projectors are pure

A projector's writes and its checkpoint commit in one transaction, so anything it does outside
that transaction happens again on every failed commit and every rebuild. A projector reads events
and writes its own projection; nothing else. Sending a command, calling an HTTP API, publishing a
notice, or enqueuing work belongs in a reactor, which exists precisely because those effects
cannot share the projection's transaction. `PORTIA100` warns when a projector takes a known effect
dependency. It recognizes common Portia, HTTP, mail, Stripe, EF Core, ADO.NET, and generated gRPC
client types through their ancestry. It is a best-effort heuristic: an application-defined gateway
can still cause an effect without a known marker. `PORTIA101` likewise warns about known DI
service-location dependencies and semantic `ActivatorUtilities` calls. Architecture review
remains responsible for arbitrary application behavior.

That is also why `IProjectorContext` carries checkpoint identity and rebuild metadata and nothing
else, and why application dependencies arrive through the constructor.

Projector handlers receive the current event separately from `IProjectorContext` because one
projection context describes the transaction or bounded batch shared by several events; binding
an event to it would give that shared context conflicting identities. Reactor contexts have the
opposite shape: each `IReactorContext<TEvent>` binds one triggering event to its own causal system
execution, so child commands and direct effects retain the right cause even inside a batch.

## Constructor-injected repository

The projector receives its repository. Keep the framework contract off the application interface
and put both on the class, so a caller that only wants `GetBalanceAsync` does not also depend on
`LoadCheckpointAsync` and `BeginAsync`:

```csharp
public interface IAccountRepository
{
    ValueTask IncrementBalanceAsync(Uuid accountId, int amount, CancellationToken ct);
    ValueTask<int> GetBalanceAsync(Uuid accountId, CancellationToken ct);
}

public sealed class AccountRepository : IAccountRepository, IProjectionStore
{
    // One class, one connection, one unit of work; two interfaces, two audiences.
}

public sealed partial class AccountProjector(IAccountRepository accounts, IProjectionStore store)
    : BatchProjector(store, EventStreamPattern.ForPattern("accounts", "balances")),
      IProjectorHandler<MoneyDeposited>
{
    public ValueTask HandleAsync(
        MoneyDeposited ev, IProjectorContext context, CancellationToken ct)
        => accounts.IncrementBalanceAsync(ev.Metadata.AggregateId, ev.Amount, ct);
}
```

```csharp
services.AddScoped<AccountRepository>();
services.AddScoped<IAccountRepository>(sp => sp.GetRequiredService<AccountRepository>());
services.AddScoped<IProjectionStore>(sp => sp.GetRequiredService<AccountRepository>());
services.AddPortia()
    .AddProjector<AccountProjector>(WorkloadScope.PerTenant);
```

Both interfaces must resolve to the *same scoped instance*, which is what makes the projector's
writes and its checkpoint share one unit of work — hence the two forwarding registrations rather
than two independent ones.

For a small projection with no other reader, `interface IAccountRepository : IProjectionStore`
and a single registration is a reasonable shortcut. It stops being one as soon as anything else
consumes the repository: every such caller then depends on the checkpoint API, and every test
double for it has to implement `BeginAsync`.

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
    : BatchProjector(accounts, EventStreamPattern.ForPattern("accounts", "balances")),
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
a batch base (`PORTIA017`), and one event type cannot select both handler modes (`PORTIA028`).
At 512 events on the maintained benchmark machine, the single-event path measured 45.27 us and
102,600 B, while a bounded batch measured 5.55 us and 12,864 B. See
[performance and scaling](performance-and-scaling.md) for reproduction details and the semantic
tradeoff; select batching for throughput only when one batch is the intended atomic boundary.

Manual implementations can override `ProjectEventAsync` / `ProjectBatchAsync`, or
`ReactToEventAsync` / `ReactBatchAsync`, instead of using generated typed handlers.

## Reactions

A base processor skips event types for which it has no handler, including manually implemented
processors. A component instance can be bound repeatedly to the same workload identity, but it
cannot be rebound to another identity; persisted checkpoints under the selected identity remain
the resume and handoff state.

A reactor performs an application effect after observing an event. There are two ordinary
patterns; choose the one that describes the behavior rather than wrapping every effect in a
command mechanically.

Dispatch a command when the behavior is a reusable application operation, needs the request
authorization and handler pipeline, or can also originate outside the reactor:

```csharp
public sealed partial class AccountReactor(
    IProjectionCheckpointStore checkpoints,
    IRequestBus bus)
    : Reactor(checkpoints, EventStreamPattern.ForPattern("accounts", "balances")),
      IReactorHandler<MoneyDeposited>
{
    public ValueTask HandleAsync(IReactorContext<MoneyDeposited> context, CancellationToken ct) =>
        bus.SendReactionAsync(
            new SendDepositReceipt(context.Trigger.Metadata.AggregateId), context, ct);
}
```

`SendReactionAsync` creates a child request from that event's reaction context, preserving its
system actor, correlation, and causation. A failed command result throws
`ReactionCommandFailedException`, so the reactor does not silently checkpoint a failed effect.
Result-bearing requests use `SendAsync<T>` directly because the reactor must decide what the
returned value and expected failures mean.

`CreateEffectId(context, effectName)` derives a stable UUID from the reactor/checkpoint identity,
source event ID, and effect name for use with an idempotent target. This does not make arbitrary
effects exactly once: the effect and checkpoint are still not one atomic transaction.
Its workload-identity text deliberately freezes the exact legacy persisted derivation. The
explicit formatter prevents a future record-shape change from altering keys; it does not create
new effect IDs, change any UUID already stored by an application, or require a migration.

Call an injected integration service directly when the action is inherently caused by the event
and adding a request contract would add no useful application boundary:

```csharp
public interface IAccountReactions
{
    ValueTask SendReceiptAsync(MoneyDeposited ev, IExecutionContext context, CancellationToken ct);
}

public sealed partial class AccountReactor(IAccountReactions accounts, IProjectionCheckpointStore checkpoints)
    : BatchReactor(checkpoints, EventStreamPattern.ForPattern("accounts", "balances")),
      IBatchReactorHandler<MoneyDeposited>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<IReactorContext<MoneyDeposited>> contexts, CancellationToken ct)
    {
        foreach (var context in contexts)
            await accounts.SendReceiptAsync(context.Trigger, context, ct);
    }
}
```

Register `IAccountReactions` and `IProjectionCheckpointStore` through ordinary scoped DI and the
reactor through `portia.AddReactor<AccountReactor>(WorkloadScope.PerTenant)`. One class may
implement both; forward the two registrations to the same scoped instance as above when it does.

Every event retains its own system execution identity and causation. Do not use the
first event's context for the whole batch. Checkpoints advance only after processing
succeeds, but external effects can already have occurred when a later effect or checkpoint
write fails. Both command handlers reached from reactors and direct effects must tolerate replay;
batching is not an external transaction or an exactly-once guarantee. Use the triggering
`DomainEventMetadata.EventId` as the deduplication key when the target supports one.

## Fitz KV persistence

An application already running on `Portia.Fitz` has a durable store it is connected to, and
`Portia.Fitz` bundles both halves of the storage contract against it. Neither one is a turnkey
projection: a projection is the application's own data model, so the framework supplies the
transaction and the checkpoint and the application supplies the reads and writes that share them.

`AddFitz` publishes the shared connection's `IKvClient`. A projection repository takes it as an
ordinary constructor dependency and derives from `FitzKvProjectionStore`, whose `Transaction`
property is the open unit of work the repository's own operations write through:

```csharp
public sealed class AccountRepository(IKvClient kv)
    : FitzKvProjectionStore(kv, "kv://accounts/balances/projection"), IAccountRepository
{
    // Key and value encoding belong to the projection, not the framework: Portia stores only its
    // own checkpoint in this route and never interprets the application's keys.
    public async ValueTask IncrementBalanceAsync(Uuid accountId, int amount, CancellationToken ct)
    {
        var key = Encoding.UTF8.GetBytes($"balance\0{accountId}");
        var current = await Transaction.GetAsync(key, ct);
        var balance = current.Found ? BinaryPrimitives.ReadInt64BigEndian(current.Value!.Value.Span) : 0;
        var next = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(next, balance + amount);
        await Transaction.PutAsync(key, next, ct);
    }
}
```

```csharp
services.AddScoped<AccountRepository>();
services.AddScoped<IAccountRepository>(sp => sp.GetRequiredService<AccountRepository>());
services.AddScoped<IProjectionStore>(sp => sp.GetRequiredService<AccountRepository>());
services.AddPortia()
    .AddFitz(configuration, fitz => fitz.UseKvCheckpoints("kv://accounts/progress/checkpoints"))
    .AddProjector<AccountProjector>(WorkloadScope.PerTenant)
    .AddReactor<WelcomeMailer>(WorkloadScope.PerTenant);
```

The two forwarding registrations are the same requirement stated above: `IAccountRepository` and
`IProjectionStore` must resolve to one scoped instance, because that instance owns the transaction
both the projection write and the checkpoint commit belong to. One store instance owns one open
transaction, so a second `BeginAsync` before the first batch commits or disposes is rejected rather
than silently retargeting the first one's writes.

`UseKvCheckpoints` covers reactors instead. Their effects can never join a transaction, so their
progress is an independent durable write, and one route holds all of it — the stored key derives
from the complete `CheckpointIdentity`, so components, tenants, and rebuild generations already
separate within a route. It is an explicit selection rather than a default: a reactor that silently
fell back to an in-memory checkpoint would reissue its entire backlog of external effects after
every restart. Applications that want per-tenant routes register their own scoped
`FitzKvCheckpointStore` from `WorkloadContext` instead of calling this.

Fitz KV locks a route at BEGIN rather than detecting the conflict at COMMIT, so a losing writer is
rejected before staging anything. Both stores translate that into the adapter-neutral
`ProjectionConcurrencyException` a hosted component already knows to treat as retryable, whether it
surfaces at begin or at commit.

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

These are implementation requirements for a backend Portia does not bundle. Fitz KV is the
exception described above; no relational or document-database adapter is bundled. The framework
cannot predict how many backend writes an event causes. Applications must select suitable
batch sizes and their repositories must enforce backend limits without partially committing
an oversized projection batch. Test atomicity, conditional conflicts, cancellation,
and ambiguous commit responses against the chosen backend.

`Portia.Testing` turns the storage requirements into executable suites (all in the
`Cntryl.Portia.Testing` namespace):

- `ProjectionStoreConformance` verifies atomic data/checkpoint commits, rollback, optimistic
  conflicts reported as the adapter-neutral `ProjectionConcurrencyException`, authoritative
  reloads, and rebuild isolation.
- `EventStoreConformance` verifies append ordering, offset resumption, and that a stale append
  fails with `EventStreamConcurrencyException` and writes nothing.
- `ReactionDeduplicationConformance` verifies an optional application deduplication primitive.
  It cannot prove crash atomicity between an external effect and its bookkeeping; use an
  idempotent sink or an integration-specific transactional inbox/outbox when that guarantee is
  required.

The suites accept small probe implementations and throw `ConformanceViolationException`, so an
application can invoke them from its normal test framework. The bundled Fitz adapters run them
too: `FitzEventStore` and `FitzKvProjectionStore` are verified against a real broker in the
repository's own integration suite, and any future official adapter must pass the applicable
suites before it ships.

Hosted passes retain their last committed checkpoint when application handling or persistence
fails. Retries back off exponentially from the workload's poll interval, up to
`MaximumFailureDelay`; after `FailureAttemptLimit` consecutive failures the hosted worker faults
with `WorkloadFailureException`. This makes a poison event terminal and observable without
silently skipping it. A successful pass resets both the count and the backoff.

`Portia.Testing` supplies backend-neutral `ProjectionStoreConformance` and optional
`ReactionDeduplicationConformance` suites. Implement
their small probe interfaces in the application's storage test project and run `VerifyAsync` from
the test framework already in use. The deduplication suite proves duplicate suppression but cannot
prove crash atomicity between an external effect and bookkeeping; use an idempotent target or a
transactional inbox/outbox when that failure window matters.

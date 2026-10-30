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

All typed handlers receive a context and an explicit cancellation token. Request handlers receive
`IRequestContext<TRequest>`, projector handlers receive `IProjectorContext<TEvent>`, and reactor
handlers receive `IReactorContext<TEvent>`. Each returns `ValueTask<Result>` (requests may also
return `Result<T>`). Application dependencies arrive through constructors.

A projector context carries its typed `Trigger`, the actual `Source` record, and `Batch` metadata
shared by every event in that atomic unit of work. `Identity` and `IsRebuild` forward to `Batch`.
Each event gets its own context, including in batch handlers. The shared non-generic
`IProjectorContext` describes the batch and contains no event or execution authority.
Reactor contexts carry independent system execution authority for each triggering event.

## Constructor-injected repository

A read model is a simple data contract. A repository owns persistence and queries for that data;
a projector applies committed events through the repository. For example, `AccountReadModel` is the
returned data, `AccountRepository` stores and retrieves it, and `AccountProjector` updates it.

Keep query interfaces focused on application reads. The concrete repository also implements
`IProjectionStore`, which lets the projector pass that same instance into its base constructor:

```csharp
public sealed record AccountReadModel(Uuid AccountId, long Balance);

public interface IAccountQueries
{
    ValueTask<AccountReadModel?> GetAsync(TenantId tenant, Uuid accountId, CancellationToken ct);
}

public sealed class AccountRepository : IAccountQueries, IProjectionStore
{
    // Application queries and updates use the same repository-managed transaction.
    // IProjectionStore supplies Portia's checkpoint and transaction lifecycle.
}

public sealed partial class AccountProjector(AccountRepository accounts)
    : BatchProjector(accounts, EventStreamPattern.ForTenant("balances")),
      IProjectorHandler<MoneyDeposited>
{
    public async ValueTask<Result> HandleAsync(
        IProjectorContext<MoneyDeposited> context, CancellationToken ct)
    {
        var ev = context.Trigger;
        await accounts.IncrementBalanceAsync(ev.Metadata.AggregateId, ev.Amount, ct);
        return Result.Success;
    }
}
```

```csharp
services.AddScoped<AccountRepository>();
services.AddScoped<IAccountQueries>(sp => sp.GetRequiredService<AccountRepository>());
services.AddPortia()
    .AddProjector<AccountProjector>("AccountProjector", WorkloadScope.PerTenant);
```

The projector receives one repository instance for data writes and progress. Query callers receive
the application-facing interface. A projector-only write interface may inherit `IProjectionStore`
when the application wants to inject a test double; the runnable Accounts example uses that shape.
The query interface remains independent of checkpoint lifecycle.

`Cntryl.Portia.Analyzers` supplies the generator for typed dispatch independently of the runtime
package used to compose the application.
`IProjectorContext` contains checkpoint identity and rebuild metadata, never application
services. `WorkloadScope.PerTenant` requires `EventStreamPattern.ForTenant(...)` and binds its realm
to the active tenant ID before checkpoints or reads; `WorkloadScope.Global` requires an exact
`ForPattern(...)` realm. Hosted workloads require an explicit stable ID in
`AddProjector` or `AddReactor`; that ID is also their checkpoint component name.

`IProjectionStore` exposes `LoadCheckpointAsync(identity, ct)` and
`BeginAsync(ProjectionBatchContext, ct)`. The latter returns `IProjectionBatch`, a lifecycle
handle with `CommitAsync(checkpoint, ct)` and `DisposeAsync()`. The repository itself
receives every application write during that unit of work. No extra projection-port
interface or target adapter is required.

The repository may use Cassie, PostgreSQL, Snowflake, or another read-model backend directly. It
retains that backend's native schema, client, and query capabilities; Portia requires no generic
table, graph, time-series, or vector abstraction. A backend that can commit repository mutations
and the checkpoint atomically implements this projection boundary. A backend or ingestion path
that cannot do so belongs behind an at-least-once reactor with application-owned idempotency.

Commit must atomically persist both changes and progress. Disposal releases resources
and discards uncommitted changes; it never commits. `ProjectionBatchContext.Checkpoint`
provides the expected starting progress for conditional writes. Use the complete
`CheckpointIdentity` to separate tenants, components, and rebuild generations. Scoped
repositories may inject `WorkloadContext` to inspect the current workload identity.

## Bulk handling

A batch base accepts ordinary per-event handlers, applying them in source order inside
one unit of work. To issue bulk application operations, implement a batch handler:

```csharp
public sealed partial class AccountProjector(AccountRepository accounts)
    : BatchProjector(accounts, EventStreamPattern.ForTenant("balances")),
      IBatchProjectorHandler<MoneyDeposited>
{
    public async ValueTask<Result> HandleAsync(
        IReadOnlyList<IProjectorContext<MoneyDeposited>> contexts, CancellationToken ct)
    {
        foreach (var context in contexts)
            await accounts.IncrementBalanceAsync(context.Trigger.Metadata.AggregateId, context.Trigger.Amount, ct);
        // A repository-specific bulk operation can replace this loop.
        return Result.Success;
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
[Performance and scaling](performance-and-scaling.md) measures both paths at 512 events and
explains the semantic tradeoff; select batching for throughput only when one batch is the intended
atomic boundary.

Manual implementations can override `ProjectEventAsync` / `ProjectBatchAsync`, or
`ReactToEventAsync` / `ReactBatchAsync`, instead of using generated typed handlers.

## Results and failure recovery

Return `Result.Success` only after the handler's work succeeds. Return a failed `Result` with
`RequestError.IsTransient = true` when retrying may succeed. A permanent result faults the hosted
worker immediately. Transient results and unclassified exceptions use the workload's bounded
exponential backoff, capped by `MaximumFailureDelay`, and fault at `FailureAttemptLimit`.
Database exceptions use the provider's `IsTransient` classification. Invalid arguments, disposed
objects, and unsupported operations stop immediately. Other unexpected exceptions receive bounded
retries rather than an unlimited retry loop.
Every retry creates a fresh DI scope and reloads authoritative progress. A successful pass resets
the consecutive-failure count. Cancellation requested by the worker stops processing normally;
an unrelated cancellation exception is a failed operation and receives bounded retries.

Dispatch stops at the first failed result. A projector disposes its uncommitted transaction;
a reactor leaves its checkpoint unchanged, although effects already performed may be repeated.
A successful batch is the unit of progress, so earlier committed batches remain committed.
`default(Result)` is invalid and faults rather than silently advancing progress.

Manual `ProjectEventAsync`, `ProjectBatchAsync`, `ReactToEventAsync`, and `ReactBatchAsync` overrides
also return `ValueTask<Result>`. Unhandled events return success and advance source progress.

Manual runners surface `ProcessorFailureException`. Its `Identity`, `Stage`, `Checkpoint`,
`FirstSource`, and `LastSource` identify the failing operation and source range. `Checkpoint` is
null when loading progress failed. `Error` retains a
returned failure; `InnerException` retains a thrown exception. A secondary transaction or source-enumerator cleanup
failure is retained in `CleanupException` rather than replacing the original failure. Source and
tenant values stay in diagnostic properties rather than framework-generated exception messages.

A commit exception can have an uncertain outcome. The diagnostic checkpoint is the last progress
confirmed by this attempt; reloading the store determines what actually committed. Never retry
using the diagnostic alone. Hosted workers perform that reload automatically.

Hosted failures produce structured logs with the component, stage, attempt, retry or stop decision,
and planned delay, alongside the existing worker-failure telemetry. A terminal
`WorkloadFailureException` retains the pass diagnostic and faults the worker; no event is skipped.

## Business outcome validation

A successful cross-aggregate read is advisory. When an operation relies on that assumption,
reactors and commands must obtain authoritative participant outcomes through choreography and
resolve failures by rejection, compensation, or intervention. For example, a positive stock lookup
still requires `InventoryReserved` or `InventoryReservationRejected` before the Order resolves.

A business rejection can be successfully processed: after durably recording the required Order
rejection or compensation request, return `Result.Success`. A failed processing `Result` means
required work remains incomplete and must prevent checkpoint advancement. Framework retries and
worker faults do not replace business outcome validation. See
[validation authority](validation-authority.md).

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
    : Reactor(checkpoints, EventStreamPattern.ForTenant("balances")),
      IReactorHandler<MoneyDeposited>
{
    public ValueTask<Result> HandleAsync(IReactorContext<MoneyDeposited> context, CancellationToken ct) =>
        bus.SendReactionAsync(
            new SendDepositReceipt(context.Trigger.Metadata.AggregateId), context, ct);
}
```

`SendReactionAsync` creates a child request from that event's reaction context, preserving its
system actor, correlation, and causation. It returns the command's `Result`; returning that outcome
from the reactor lets Portia enforce its retry policy and prevents checkpointing a failed effect.
Result-bearing requests use `SendAsync<T>` directly because the reactor decides how their value
maps to its own outcome.

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
    : BatchReactor(checkpoints, EventStreamPattern.ForTenant("balances")),
      IBatchReactorHandler<MoneyDeposited>
{
    public async ValueTask<Result> HandleAsync(
        IReadOnlyList<IReactorContext<MoneyDeposited>> contexts, CancellationToken ct)
    {
        foreach (var context in contexts)
            await accounts.SendReceiptAsync(context.Trigger, context, ct);
        return Result.Success;
    }
}
```

Register `IAccountReactions` and `IProjectionCheckpointStore` through ordinary scoped DI and the
reactor through `portia.AddReactor<AccountReactor>("AccountReactor", WorkloadScope.PerTenant)`. One class may
implement both; forward the two registrations to the same scoped instance as above when it does.

Every event retains its own system execution identity and causation. Do not use the
first event's context for the whole batch. Checkpoints advance only after processing
succeeds, but external effects can already have occurred when a later effect or checkpoint
write fails. Both command handlers reached from reactors and direct effects must tolerate replay;
batching is not an external transaction or an exactly-once guarantee. Use the triggering
`DomainEventMetadata.EventId` as the deduplication key when the target supports one.

## Cassie persistence

Use `CassieProjectionRepository` for native Cassie SQL updates and queries with an atomic Portia
checkpoint. `AddCassie(...).AddCassieProjection<TProjector, TRepository>(...)` supplies shared
connections, metadata initialization, scoped repositories, and the standard runner. The
[Cassie guide](cassie-projections.md) shows the data contract, repository, projector, and registration.

## Fitz KV persistence

An application already running on `Cntryl.Portia.Fitz` has a durable store it is connected to, and
`Cntryl.Portia.Fitz` bundles both halves of the storage contract against it. Neither one is a turnkey
projection: a projection is the application's own data model, so the framework supplies the
transaction and the checkpoint and the application supplies the reads and writes that share them.

`AddFitz` publishes the shared connection's `IKvClient`. A projection repository takes it as an
ordinary constructor dependency and derives from `FitzKvProjectionStore`, whose `Transaction`
property is the open unit of work the repository's own operations write through:

```csharp
public sealed class AccountRepository(IKvClient kv)
    : FitzKvProjectionStore(kv, "kv://accounts/balances/projection", Projector), IAccountQueries
{
    // The projector this repository serves, and so the name it must be registered under.
    public const string Projector = "AccountProjector";

    // Key and value encoding belong to the projection, not the framework: Portia stores only its
    // own checkpoint alongside them and never interprets the application's keys.
    public async ValueTask IncrementBalanceAsync(Uuid accountId, int amount, CancellationToken ct)
    {
        var key = Encoding.UTF8.GetBytes($"balance\0{accountId}");
        var current = await Transaction.GetAsync(key, ct);
        var balance = current.Found ? BinaryPrimitives.ReadInt64BigEndian(current.Value!.Value.Span) : 0;
        var next = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(next, balance + amount);
        await Transaction.PutAsync(key, next, ct);
    }

    // Query-side reads run outside any batch, so they open their own read-only transaction on the
    // resource the projector writes for this tenant.
    public async ValueTask<AccountReadModel?> GetAsync(TenantId tenant, Uuid accountId, CancellationToken ct)
    {
        await using var tx = await BeginTenantReadAsync(tenant, ct);
        var current = await tx.GetAsync(Encoding.UTF8.GetBytes($"balance\0{accountId}"), ct);
        return current.Found
            ? new AccountReadModel(accountId, BinaryPrimitives.ReadInt64BigEndian(current.Value!.Value.Span))
            : null;
    }
}
```

```csharp
services.AddScoped<AccountRepository>();
services.AddScoped<IAccountQueries>(sp => sp.GetRequiredService<AccountRepository>());
services.AddPortia()
    .AddFitz(configuration, fitz => fitz.UseKvCheckpoints("kv://accounts/progress/checkpoints"))
    .AddProjector<AccountProjector>(AccountRepository.Projector, WorkloadScope.PerTenant)
    .AddReactor<WelcomeMailer>("WelcomeMailer", WorkloadScope.PerTenant);
```

The query interface forwards to the scoped repository; the projector receives that same concrete
repository for its data and checkpoint transaction. One repository instance owns one open
transaction, so a second `BeginAsync` before the first batch commits or disposes is rejected rather
than silently retargeting the first one's writes.

The route a repository is constructed with is a base, not the resource every batch writes. Fitz KV
locks a whole resource for a read-write transaction's lifetime, not just the keys it touches, so
Portia derives one resource per workload — one projector in one realm, and so one per tenant of a
`PerTenant` projector — and no two workloads can contend for a lock however the routes are
configured.

A repository serves exactly one projector, named when it is constructed. Reads outside a batch open
`BeginTenantReadAsync(tenant)` for a `PerTenant` projector, or `BeginReadAsync(realm)` with the realm
of a global projector's pattern — always read-only, so a query never holds the lock the projector
writes under.
It is refused while a batch is open on the same repository instance: that read would see only
committed data, never the batch's staged writes, so a write method reads through `Transaction`, as
`IncrementBalanceAsync` does above.
The name must be the ID the projector is registered under with `AddProjector`, which is also its
checkpoint name. A hosted projector whose repository was built for any other name stops the host
when it starts, and a repository driven outside hosting throws on its first checkpoint load, rather
than committing data to a resource its reads never open.

A read opens one realm, so a `PerTenant` projector's data is only ever read one tenant at a time.
A view across tenants is a different query with its own projector: register a global projector that
builds that read model, rather than opening every tenant's resource in turn.

`UseKvCheckpoints` covers reactors instead. Their effects can never join a transaction, so their
progress is an independent KV write. Its route is a base as well: one checkpoint store serves
every reactor, and each reactor in each tenant gets its own derived resource, so one tenant's save
never waits on another's. It is an explicit selection rather than a default: a reactor that silently
fell back to an in-memory checkpoint would reissue its entire backlog of external effects after
every restart.

Fitz KV commits default to `KvDurability.Buffered`; stream appends remain synchronous. Projection
data and checkpoint advance atomically, but a crash can rewind recent KV progress and trigger
replay. Reactor effects must tolerate replay regardless of persistence mode. Select synchronous
KV commits explicitly with `UseKvCheckpoints(route, KvDurability.Sync)`, the four-argument
`FitzKvProjectionStore` base constructor, or the three-argument tenant snapshot store constructor.

Checkpoint values use a fixed format prefix followed by the backend-owned cursor as UTF-8. Readers
also accept the Portia 0.1.x representation—exactly eight bytes containing an unsigned big-endian
offset—and expose it as an invariant decimal `EventCursor`. Unknown unversioned values are rejected;
loading is non-mutating, and the next successful checkpoint commit upgrades a legacy value to the
current versioned representation.

Fitz KV locks a route at BEGIN rather than detecting the conflict at COMMIT, so a losing writer is
rejected before staging anything. Both stores translate that into the adapter-neutral
`ProjectionConcurrencyException` a hosted component already knows to treat as retryable, whether it
surfaces at begin or at commit.

### Paged directories

A read model that callers list, sort, and page — teams, members, documents — fits
`Cntryl.Fitz.Extensions`' `KvDirectory<T, TKey>`: its declared covering indexes make each page one
bounded range scan with a keyset cursor. Every directory operation takes an open `IKvTransaction`,
so it composes with both halves of the repository: the projector writes through `Transaction`, and
queries read through `BeginTenantReadAsync`.

```csharp
// Like every domain event, both are also [JsonSerializable] roots on the application's [PortiaJsonContext].
[Discriminator("team.created")]
public sealed record TeamCreated(string Name) : DomainEvent;

[Discriminator("team.renamed")]
public sealed record TeamRenamed(string Name) : DomainEvent;

public sealed record Team(Uuid Id, string Name);

[JsonSerializable(typeof(Team))]
sealed partial class TeamJsonContext : JsonSerializerContext;

public interface ITeamDirectory
{
    ValueTask<Team?> GetAsync(TenantId tenant, Uuid id, CancellationToken ct);
    ValueTask<Page<Team>> ListAsync(TenantId tenant, int take, string? cursor, CancellationToken ct);
}

public sealed class TeamRepository(IKvClient kv)
    : FitzKvProjectionStore(kv, "kv://directory/teams/projection", Projector), ITeamDirectory
{
    public const string Projector = "TeamProjector";

    static readonly KvDirectoryIndex<Team> ByName = new(
        "by_name", 1, static team => [team.Name.ToUpperInvariant()]);

    static readonly KvDirectory<Team, Uuid> Teams = new(
        "teams", TeamJsonContext.Default.Team, static team => team.Id, static id => [id.ToGuid()], [ByName]);

    // Projector side: every write joins the batch's transaction, so it commits with the checkpoint.
    public ValueTask AddAsync(Team team, CancellationToken ct) => Teams.InsertAsync(Transaction, team, ct);

    public async ValueTask RenameAsync(Uuid id, string name, CancellationToken ct)
    {
        // The previous value names the index rows to replace. It is read through Transaction, not
        // BeginReadAsync, which is refused mid-batch because it cannot see the batch's own writes.
        var current = await Teams.GetAsync(Transaction, id, ct)
            ?? throw new InvalidOperationException($"Team '{id}' was renamed before it was created.");
        await Teams.ReplaceAsync(Transaction, current, current with { Name = name }, ct);
    }

    // Query side: one read-only transaction per call, on the resource the projector writes for this tenant.
    public async ValueTask<Team?> GetAsync(TenantId tenant, Uuid id, CancellationToken ct)
    {
        await using var tx = await BeginTenantReadAsync(tenant, ct);
        return await Teams.GetAsync(tx, id, ct);
    }

    public async ValueTask<Page<Team>> ListAsync(TenantId tenant, int take, string? cursor, CancellationToken ct)
    {
        await using var tx = await BeginTenantReadAsync(tenant, ct);
        return await Teams.QueryAsync(tx, ByName.Query().Take(take).After(cursor), ct);
    }
}

public sealed partial class TeamProjector(TeamRepository teams)
    : BatchProjector(teams, EventStreamPattern.ForTenant("teams")),
      IProjectorHandler<TeamCreated>, IProjectorHandler<TeamRenamed>
{
    public async ValueTask<Result> HandleAsync(IProjectorContext<TeamCreated> context, CancellationToken ct)
    {
        await teams.AddAsync(new Team(context.Trigger.Metadata.AggregateId, context.Trigger.Name), ct);
        return Result.Success;
    }

    public async ValueTask<Result> HandleAsync(IProjectorContext<TeamRenamed> context, CancellationToken ct)
    {
        await teams.RenameAsync(context.Trigger.Metadata.AggregateId, context.Trigger.Name, ct);
        return Result.Success;
    }
}
```

```csharp
services.AddScoped<TeamRepository>();
services.AddScoped<ITeamDirectory>(sp => sp.GetRequiredService<TeamRepository>());
services.AddPortia()
    .AddFitz(configuration)
    .AddProjector<TeamProjector>(TeamRepository.Projector, WorkloadScope.PerTenant);
```

`FitzKvDirectoryProjectionTests` runs this example against a real broker — only the base route is
injected, so each run writes its own resources — from appended events through the projector pass
to paged reads.

The projector takes the concrete repository and passes it as its own store, so its writes and its
checkpoint share one instance without a forwarding registration; query callers depend only on
`ITeamDirectory` and never see the checkpoint API. A page's `NextCursor` goes back to the caller as
an opaque string. It is bound to the tenant's resource, so a cursor from one tenant's list is
rejected with `KvDirectoryQueryError.CursorMismatch` on another's rather than paging from a foreign
key. The caller-owned `QueryAsync` overload needs `Cntryl.Fitz.Extensions` 1.4.0 or later with
`Cntryl.Fitz.Core` 1.4.0 or later. `Cntryl.Portia.Fitz` supplies the core package but not the
extensions, so reference `Cntryl.Fitz.Extensions` 1.4.0 or later directly.

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

`Cntryl.Portia.Testing` turns the storage requirements into executable suites (all in the
`Cntryl.Portia.Testing` namespace):

- `ProjectionStoreConformance` verifies atomic data/checkpoint commits, rollback, optimistic
  conflicts reported as the adapter-neutral `ProjectionConcurrencyException`, authoritative
  reloads, rebuild isolation, and that checkpoints for another realm or area are independent.
- `EventStoreConformance` verifies append ordering, offset resumption, that an append expecting
  a position behind or ahead of the stream fails with `EventStreamConcurrencyException` and
  writes nothing, and that a pattern read excludes other realms and areas.
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

`Cntryl.Portia.Testing` supplies backend-neutral `ProjectionStoreConformance` and optional
`ReactionDeduplicationConformance` suites. Implement
their small probe interfaces in the application's storage test project and run `VerifyAsync` from
the test framework already in use. The deduplication suite proves duplicate suppression but cannot
prove crash atomicity between an external effect and bookkeeping; use an idempotent target or a
transactional inbox/outbox when that failure window matters.

The broader first-party Cassie and userland storage boundary is recorded in
[platform vision](platform-vision.md).

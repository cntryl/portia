# Cassie projection repositories

`Cntryl.Portia.Cassie` connects Portia projectors to Cassie's native SQL transactions through
`Cntryl.Cassie` 0.1.0. A repository owns persistence and queries. A read model is the simple data
contract returned by those queries. A projector applies committed events through the repository.

## Composition

```csharp
services.AddSingleton<ITenantDirectory>(new FixedTenantDirectory(new TenantId("demo")));
services.AddPortia()
    .AddCassie(connectionString)
    .AddCassieProjection<AccountProjector, AccountRepository>(
        AccountRepository.ProjectorName, WorkloadScope.PerTenant)
    .AddFitz(configuration.GetSection("Fitz"))
    .AddWorkers();
```

`AddCassie` registers one shared native data source and `CassieProjectionDatabase`.
`AddCassieProjection` registers a scoped repository and the normal Portia projector workload.
Queries can inject `AccountRepository` directly. Forward a query interface to that scoped instance
when the application needs one; query interfaces need no checkpoint or transaction methods.

The library initializes its checkpoint table automatically before use. Application migrations own
application tables and indexes. For the example below, create this native Cassie table before
starting workers:

```sql
CREATE TABLE account_balances (
    account_key TEXT PRIMARY KEY,
    balance_cents BIGINT NOT NULL
);
```

## Data and repository

The read model carries data and has no persistence behavior:

```csharp
public sealed record AccountReadModel(Uuid AccountId, long BalanceCents);
```

The repository owns SQL. `CreateCommand` enlists updates in the current batch transaction;
`CurrentProjectionKey` identifies its component, bound stream pattern, and rebuild generation.
Native application keys and query predicates must include that scope. The library does not rewrite
application SQL or establish query authorization.

```csharp
public sealed class AccountRepository(CassieProjectionDatabase database)
    : CassieProjectionRepository(database, ProjectorName)
{
    public const string ProjectorName = "AccountProjector";

    public async ValueTask<Result> OpenAsync(Uuid accountId, long balanceCents, CancellationToken ct)
    {
        await using var command = CreateCommand(
            "INSERT INTO account_balances (account_key, balance_cents) VALUES (@key, @balance)");
        command.Parameters.Add(new CassieParameter { ParameterName = "key", Value = CurrentProjectionKey + accountId });
        command.Parameters.Add(new CassieParameter { ParameterName = "balance", Value = balanceCents });
        await command.ExecuteNonQueryAsync(ct);
        return Result.Success;
    }

    public async ValueTask<Result> ApplyDepositAsync(Uuid accountId, long amountCents, CancellationToken ct)
    {
        await using var command = CreateCommand(
            "UPDATE account_balances SET balance_cents = balance_cents + @amount WHERE account_key = @key");
        command.Parameters.Add(new CassieParameter { ParameterName = "key", Value = CurrentProjectionKey + accountId });
        command.Parameters.Add(new CassieParameter { ParameterName = "amount", Value = amountCents });
        return await command.ExecuteNonQueryAsync(ct) == 1
            ? Result.Success
            : RequestError.Validation("A deposit was projected before its account was opened.");
    }

    public async ValueTask<AccountReadModel?> GetAsync(TenantId tenant, Uuid accountId, CancellationToken ct)
    {
        var identity = new CheckpointIdentity(ProjectorName, EventStreamPattern.ForPattern(tenant.Value, "accounts"));
        await using var connection = await OpenReadAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT balance_cents FROM account_balances WHERE account_key = @key";
        command.Parameters.Add(new CassieParameter { ParameterName = "key", Value = GetProjectionKey(identity) + accountId });
        var balance = await command.ExecuteScalarAsync(ct);
        return balance is null or DBNull ? null : new AccountReadModel(accountId, Convert.ToInt64(balance));
    }
}
```

Import `Cntryl.Cassie.Commands` for `CassieParameter`, alongside the ordinary Portia, System,
threading, and task namespaces. A repository read inside an active batch uses `CreateCommand` so
it sees its own staged writes. `OpenReadAsync` is for committed query-side reads and refuses use
while that repository has a batch open.

## Typed projector

These handlers use the Account feature's `AccountOpened` and `FundsDeposited` events. Register
their stable discriminators and source-generated JSON roots as for any other Portia events.

```csharp
public sealed partial class AccountProjector(AccountRepository accounts)
    : BatchProjector(accounts, EventStreamPattern.ForTenant("accounts"), AccountRepository.ProjectorName),
      IProjectorHandler<AccountOpened>, IProjectorHandler<FundsDeposited>
{
    public ValueTask<Result> HandleAsync(IProjectorContext<AccountOpened> context, CancellationToken ct) =>
        accounts.OpenAsync(context.Trigger.Metadata.AggregateId, context.Trigger.InitialBalanceCents, ct);

    public ValueTask<Result> HandleAsync(IProjectorContext<FundsDeposited> context, CancellationToken ct) =>
        accounts.ApplyDepositAsync(context.Trigger.Metadata.AggregateId, context.Trigger.AmountCents, ct);
}
```

Portia begins the transaction, dispatches events in source order, commits model changes and the
opaque source cursor together, then disposes the batch. Failed results prevent commit. Permanent
results stop the hosted worker; transient results receive bounded retries in fresh scopes after
loading authoritative progress. Thrown database exceptions use the provider's transient
classification. Other failures retain the diagnostics described in
[projectors and reactors](projectors-and-reactors.md#results-and-failure-recovery).

## Validation authority

Cassie read models support advisory cross-aggregate checks. They can reject predictable failures
early, but even a successful fresh read cannot establish a cross-aggregate invariant. The owning
aggregate must enforce its local invariant, and choreography must obtain authoritative participant
outcomes for the cross-aggregate business requirement. Resolve negative outcomes through explicit
rejection, compensation, or intervention. A successful advisory check never removes this obligation;
see [validation authority](validation-authority.md).

## Supported boundary

The integration tests exercise the published client and pinned server image, including SQL model
and checkpoint atomicity, rollback, stale writers, tenant/pattern/generation isolation, repository
recreation, and hosted retry and permanent-failure handling. See
[the integration test setup](../CONTRIBUTING.md) to run them.

Rebuild IDs select isolated data and checkpoints; query code can explicitly address a candidate
identity to preview it. Automatic generation promotion, read-your-writes fences, crash/restart
durability, and graph, time-series, and vector index visibility remain separate delivery gates.
Ordinary queries read committed projection data and are eventually consistent with Fitz.

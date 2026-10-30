namespace Portia.Accounts;

/// <summary>Application repository for account commands.</summary>
public interface IAccountCommandRepository
{
    /// <summary>Opens an account and commits its event on success.</summary>
    ValueTask<Result> OpenAsync(OpenAccount request, IRequestContext context, CancellationToken ct);

    /// <summary>Deposits funds and commits the event on success.</summary>
    ValueTask<Result> DepositAsync(DepositFunds request, IRequestContext context, CancellationToken ct);
}

/// <summary>Ordinary application repository that delegates event persistence to the aggregate executor.</summary>
public sealed class AccountCommandRepository(IAggregateExecutor aggregates) : IAccountCommandRepository
{
    /// <inheritdoc />
    public ValueTask<Result> OpenAsync(OpenAccount request, IRequestContext context, CancellationToken ct) =>
        aggregates.ExecuteAsync(new Account(request.Tenant, request.AccountId), account =>
            AggregateOutcome.CommitOnSuccess(account.Open(request.InitialBalanceCents)), context, ct);

    /// <inheritdoc />
    public ValueTask<Result> DepositAsync(DepositFunds request, IRequestContext context, CancellationToken ct) =>
        aggregates.ExecuteAsync(new Account(request.Tenant, request.AccountId), account =>
            AggregateOutcome.CommitOnSuccess(account.Deposit(request.AmountCents)), context, ct);
}

/// <summary>Query-side repository for the account projection.</summary>
public interface IAccountReadRepository
{
    /// <summary>Gets one tenant's projected account, or null when no projection exists.</summary>
    ValueTask<AccountReadModel?> GetAsync(TenantId tenant, Uuid accountId, CancellationToken ct);
}

/// <summary>Repository writes performed by the pure account projector.</summary>
public interface IAccountProjectionRepository : IProjectionStore
{
    /// <summary>Reads the account value from the currently open projection transaction.</summary>
    ValueTask<AccountReadModel?> GetForUpdateAsync(Uuid accountId, CancellationToken ct);

    /// <summary>Writes the updated account value into the currently open projection transaction.</summary>
    ValueTask SaveAsync(AccountReadModel account, CancellationToken ct);
}

/// <summary>Fitz KV repository that commits account data and projector checkpoints in one transaction.</summary>
public sealed class AccountRepository : FitzKvProjectionStore, IAccountReadRepository,
    IAccountProjectionRepository
{
    /// <summary>The stable workload and checkpoint identity for this projector.</summary>
    public const string ProjectorName = "AccountProjector";

    /// <summary>Creates the projection repository for one configured Fitz KV base route.</summary>
    public AccountRepository(IKvClient client, string route)
        : base(client, route, ProjectorName)
    {
    }

    /// <inheritdoc />
    public async ValueTask<AccountReadModel?> GetAsync(TenantId tenant, Uuid accountId, CancellationToken ct)
    {
        await using var transaction = await BeginTenantReadAsync(tenant, ct).ConfigureAwait(false);
        return await ReadAsync(transaction, accountId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<AccountReadModel?> GetForUpdateAsync(Uuid accountId, CancellationToken ct) =>
        ReadAsync(Transaction, accountId, ct);

    /// <inheritdoc />
    public async ValueTask SaveAsync(AccountReadModel account, CancellationToken ct) =>
        await Transaction.PutAsync(Key(account.AccountId),
            JsonSerializer.SerializeToUtf8Bytes(account, AccountsJsonContext.Default.AccountReadModel), ct)
            .ConfigureAwait(false);

    static async ValueTask<AccountReadModel?> ReadAsync(IKvTransaction transaction, Uuid accountId,
        CancellationToken ct)
    {
        var value = await transaction.GetAsync(Key(accountId), ct).ConfigureAwait(false);
        return value.Found
            ? JsonSerializer.Deserialize(value.Value!.Value.Span, AccountsJsonContext.Default.AccountReadModel)
            : null;
    }

    static ReadOnlyMemory<byte> Key(Uuid accountId) => Encoding.UTF8.GetBytes("account\0" + accountId);
}

namespace Portia.Accounts;

/// <summary>Pure event-to-view reducer for the account read model.</summary>
public static class AccountProjection
{
    /// <summary>Applies one account event to its current projected value.</summary>
    /// <param name="current">The current view, or null before the account is opened.</param>
    /// <param name="ev">The event to fold into the view.</param>
    /// <returns>The updated view.</returns>
    public static AccountReadModel Apply(AccountReadModel? current, DomainEvent ev) => ev switch
    {
        AccountOpened opened when current is null =>
            new AccountReadModel(opened.Metadata.AggregateId, opened.InitialBalanceCents),
        FundsDeposited deposit when current is not null && current.AccountId == deposit.Metadata.AggregateId =>
            current with { BalanceCents = checked(current.BalanceCents + deposit.AmountCents) },
        AccountOpened => throw new InvalidOperationException("An account projection cannot open the same account twice."),
        FundsDeposited => throw new InvalidOperationException("An account must be opened before a deposit is projected."),
        _ => throw new ArgumentException($"Unsupported account event '{ev.GetType().Name}'.", nameof(ev))
    };
}

/// <summary>Projects account events into the ordinary account read repository.</summary>
public sealed partial class AccountProjector(IAccountProjectionRepository accounts, IProjectionStore store)
    : BatchProjector(store, EventStreamPattern.ForTenant("accounts"), AccountProjectionRepository.ProjectorName),
        IProjectorHandler<AccountOpened>, IProjectorHandler<FundsDeposited>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(AccountOpened ev, IProjectorContext context, CancellationToken ct) =>
        ApplyAsync(ev, context, ct);

    /// <inheritdoc />
    public ValueTask HandleAsync(FundsDeposited ev, IProjectorContext context, CancellationToken ct) =>
        ApplyAsync(ev, context, ct);

    async ValueTask ApplyAsync(DomainEvent ev, IProjectorContext context, CancellationToken ct)
    {
        var accountId = ev.Metadata.AggregateId;
        var current = await accounts.GetForUpdateAsync(accountId, ct).ConfigureAwait(false);
        await accounts.SaveAsync(AccountProjection.Apply(current, ev), ct).ConfigureAwait(false);
    }
}

namespace Portia.Accounts;

/// <summary>The account was opened with its initial balance.</summary>
/// <param name="InitialBalanceCents">The opening balance in cents.</param>
[Discriminator("accounts.account-opened")]
public sealed record AccountOpened(long InitialBalanceCents) : DomainEvent;

/// <summary>Funds were deposited into the account.</summary>
/// <param name="AmountCents">The deposited amount in cents.</param>
[Discriminator("accounts.funds-deposited")]
public sealed record FundsDeposited(long AmountCents) : DomainEvent;

/// <summary>An account aggregate that enforces its balance invariants on every command.</summary>
public sealed class Account : Aggregate
{
    /// <summary>Creates an account aggregate for one tenant and stable account identity.</summary>
    /// <param name="tenant">The tenant owning the account; its ID is the realm of the account's stream.</param>
    /// <param name="id">The account identity.</param>
    public Account(TenantId tenant, Uuid id)
        : base(id, new EventStreamAddress(tenant.Value, "accounts", id.ToString()))
    {
        On<AccountOpened>(ev =>
        {
            IsOpen = true;
            BalanceCents = ev.InitialBalanceCents;
        });
        On<FundsDeposited>(ev => BalanceCents = checked(BalanceCents + ev.AmountCents));
    }

    /// <summary>Gets whether the account has been opened.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Gets the current balance in cents.</summary>
    public long BalanceCents { get; private set; }

    /// <summary>Opens the account with a nonnegative starting balance.</summary>
    /// <param name="initialBalanceCents">The opening balance in cents.</param>
    /// <returns>The command result.</returns>
    public Result Open(long initialBalanceCents)
    {
        if (IsOpen)
            return RequestError.Conflict("The account is already open.");
        if (initialBalanceCents < 0)
            return RequestError.Validation("The opening balance cannot be negative.");

        RaiseEvent(new AccountOpened(initialBalanceCents));
        return Result.Success;
    }

    /// <summary>Deposits a positive amount without overflowing the account balance.</summary>
    /// <param name="amountCents">The amount to deposit in cents.</param>
    /// <returns>The command result.</returns>
    public Result Deposit(long amountCents)
    {
        if (!IsOpen)
            return RequestError.NotFound("The account is not open.");
        if (amountCents <= 0)
            return RequestError.Validation("The deposit must be positive.");
        if (amountCents > long.MaxValue - BalanceCents)
            return RequestError.Validation("The deposit exceeds the supported balance.");

        RaiseEvent(new FundsDeposited(amountCents));
        return Result.Success;
    }
}

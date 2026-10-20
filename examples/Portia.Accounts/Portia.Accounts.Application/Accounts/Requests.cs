namespace Portia.Accounts;

/// <summary>Shared account identity carried by account commands and queries.</summary>
public interface IAccountRequest : IRequestBase
{
    /// <summary>Gets the tenant that owns the account.</summary>
    string Tenant { get; }

    /// <summary>Gets the account identity.</summary>
    Uuid AccountId { get; }
}

/// <summary>Opens an account for one tenant.</summary>
/// <param name="Tenant">The tenant that owns the account.</param>
/// <param name="AccountId">The stable account identity.</param>
/// <param name="InitialBalanceCents">The nonnegative starting balance in cents.</param>
[Discriminator("accounts.open")]
[RequestRoute("accounts", "account", "*", "open")]
public sealed record OpenAccount(string Tenant, Uuid AccountId, long InitialBalanceCents)
    : IRequest, ICallable, IAccountRequest;

/// <summary>Deposits money into an existing account.</summary>
/// <param name="Tenant">The tenant that owns the account.</param>
/// <param name="AccountId">The account identity.</param>
/// <param name="AmountCents">The positive deposit amount in cents.</param>
[Discriminator("accounts.deposit")]
[RequestRoute("accounts", "account", "*", "deposit")]
public sealed record DepositFunds(string Tenant, Uuid AccountId, long AmountCents)
    : IRequest, ICallable, IAccountRequest;

/// <summary>Reads one account's projected balance.</summary>
/// <param name="Tenant">The tenant that owns the account.</param>
/// <param name="AccountId">The account identity.</param>
[Discriminator("accounts.get")]
[RequestRoute("accounts", "account", "*", "get")]
public sealed record GetAccount(string Tenant, Uuid AccountId)
    : IRequest<AccountReadModel>, IReadOnlyRequest, ICallable, IAccountRequest;

/// <summary>The account data returned by the read model.</summary>
/// <param name="AccountId">The account identity.</param>
/// <param name="BalanceCents">The projected balance in cents.</param>
public sealed record AccountReadModel(Uuid AccountId, long BalanceCents);

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OpenAccount))]
[JsonSerializable(typeof(DepositFunds))]
[JsonSerializable(typeof(GetAccount))]
[JsonSerializable(typeof(AccountReadModel))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(AccountOpened))]
[JsonSerializable(typeof(FundsDeposited))]
public sealed partial class AccountsJsonContext : JsonSerializerContext;

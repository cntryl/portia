namespace Portia.Accounts;

/// <summary>Authorizes access to account commands and reads.</summary>
public sealed class AccountRequestAuthorizer(IAccountAccess access, IAccountLifecycleObserver lifecycle)
    : IRequestAuthorizer<IAccountRequest>
{
    /// <inheritdoc />
    public ValueTask<Result> AuthorizeAsync(IRequestContext<IAccountRequest> context, CancellationToken ct)
    {
        lifecycle.Record("authorizer");
        return ValueTask.FromResult(access.CanAccess(context.Actor, context.Request.Tenant, context.Request.AccountId)
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden, "The actor cannot access this account.")));
    }
}

/// <summary>Rejects requests for tenants the configured account projector does not serve.</summary>
public sealed class AccountTenantGuard(IAccountTenantScope tenants, IAccountLifecycleObserver lifecycle)
    : IRequestGuard<IAccountRequest>
{
    /// <inheritdoc />
    public ValueTask<Result> GuardAsync(IRequestContext<IAccountRequest> context, CancellationToken ct)
    {
        lifecycle.Record("tenant.guard");
        return ValueTask.FromResult(tenants.Serves(context.Request.Tenant)
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Validation,
                "The account feature is not configured for this tenant.")));
    }
}

/// <summary>Checks operational availability immediately before an account handler runs.</summary>
public sealed class AccountOperationsGuard(IAccountOperations operations, IAccountLifecycleObserver lifecycle)
    : IRequestGuard<IAccountRequest>
{
    /// <inheritdoc />
    public ValueTask<Result> GuardAsync(IRequestContext<IAccountRequest> context, CancellationToken ct)
    {
        lifecycle.Record("guard");
        return ValueTask.FromResult(operations.IsAvailable(context.Request.Tenant)
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Conflict, "Account operations are paused.")));
    }
}

/// <summary>Records command lifecycle around the guard and handler.</summary>
public sealed class AccountRequestBehavior(IAccountLifecycleObserver lifecycle)
    : IRequestPipelineBehavior<DepositFunds>
{
    /// <inheritdoc />
    public async ValueTask<Result> HandleAsync(IRequestContext<DepositFunds> context,
        RequestPipelineNext continuation, CancellationToken ct)
    {
        lifecycle.Record("behavior.enter");
        var result = await continuation(ct).ConfigureAwait(false);
        lifecycle.Record("behavior.exit");
        return result;
    }
}

/// <summary>Opens an account through the ordinary account repository.</summary>
public sealed class OpenAccountHandler(IAccountRepository accounts, IAccountLifecycleObserver lifecycle)
    : IRequestHandler<OpenAccount>
{
    /// <inheritdoc />
    public ValueTask<Result> HandleAsync(IRequestContext<OpenAccount> context, CancellationToken ct)
    {
        lifecycle.Record("handler.open");
        return accounts.OpenAsync(context.Request, context, ct);
    }
}

/// <summary>Deposits money through the ordinary account repository.</summary>
public sealed class DepositFundsHandler(IAccountRepository accounts, IAccountLifecycleObserver lifecycle)
    : IRequestHandler<DepositFunds>
{
    /// <inheritdoc />
    public ValueTask<Result> HandleAsync(IRequestContext<DepositFunds> context, CancellationToken ct)
    {
        lifecycle.Record("handler.deposit");
        return accounts.DepositAsync(context.Request, context, ct);
    }
}

/// <summary>Reads one projected account.</summary>
public sealed class GetAccountHandler(IAccountReadRepository accounts) : IRequestHandler<GetAccount, AccountReadModel>
{
    /// <inheritdoc />
    public async ValueTask<Result<AccountReadModel>> HandleAsync(IRequestContext<GetAccount> context,
        CancellationToken ct)
    {
        var account = await accounts.GetAsync(context.Request.Tenant, context.Request.AccountId, ct)
            .ConfigureAwait(false);
        return account is null
            ? Result<AccountReadModel>.Failure(new RequestError(RequestErrorKind.NotFound, "The account was not found."))
            : Result<AccountReadModel>.Success(account);
    }
}

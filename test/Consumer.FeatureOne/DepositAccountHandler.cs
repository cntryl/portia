namespace Cntryl.Portia.Consumer;

public sealed class DepositAccountHandler(IAggregateRepository repository, IConsumerEffects effects, IConsumerScope scope)
    : IRequestHandler<DepositAccount>
{
    public async ValueTask<Result> HandleAsync(IRequestContext<DepositAccount> context, CancellationToken ct)
    {
        var request = context.Request;
        var account = await repository.LoadAsync<Account>(request.Id, ct) ?? new Account(request.Id);
        if (request.Amount <= 0)
        {
            account.Audit(new Declined("Deposit must be positive."));
            await repository.SaveAsync(account, ct);
            return Result.Failure(new RequestError(RequestErrorKind.Validation, "Deposit must be positive."));
        }
        account.Deposit(request.Amount);
        await repository.SaveAsync(account, ct);
        effects.Record("business", account.Id, request.Amount, scope.Id);
        return Result.Success;
    }
}

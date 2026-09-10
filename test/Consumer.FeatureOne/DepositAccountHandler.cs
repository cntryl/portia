namespace Cntryl.Portia.Consumer;

public sealed class DepositAccountHandler(
    IAggregateRepository repository,
    IConsumerEffects effects,
    IConsumerScope scope)
    : IRequestHandler<DepositAccount>
{
    public async ValueTask<Result> HandleAsync(IRequestContext<DepositAccount> context, CancellationToken ct)
    {
        var request = context.Request;
        var account = await repository.HydrateAsync(new Account(request.Id), ct);
        if (request.Amount <= 0)
        {
            account.Audit(new Declined("Deposit must be positive."));
            await repository.SaveAsync(account, context, ct);
            return Result.Failure(new RequestError(RequestErrorKind.Validation, "Deposit must be positive."));
        }

        account.Deposit(request.Amount);
        await repository.SaveAsync(account, context, ct);
        effects.Record("business", account.Id, request.Amount, scope.Id);
        return Result.Success;
    }
}

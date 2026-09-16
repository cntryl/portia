namespace Cntryl.Portia.Consumer;

public sealed class DepositAccountHandler(
    IAggregateExecutor aggregates,
    IConsumerEffects effects,
    IConsumerScope scope)
    : IRequestHandler<DepositAccount>
{
    public async ValueTask<Result> HandleAsync(IRequestContext<DepositAccount> context, CancellationToken ct)
    {
        var request = context.Request;
        var result = await aggregates.ExecuteAsync(new Account(request.Id), account =>
        {
            if (request.Amount <= 0)
            {
                account.Audit(new Declined("Deposit must be positive."));
                return AggregateOutcome.Commit(Result.Failure(
                    new RequestError(RequestErrorKind.Validation, "Deposit must be positive.")));
            }

            account.Deposit(request.Amount);
            return AggregateOutcome.Commit(Result.Success);
        }, context, ct);
        if (result.IsSuccess)
        {
            effects.Record("business", request.Id, request.Amount, scope.Id);
        }

        return result;
    }
}

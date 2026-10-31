namespace Cntryl.Portia.Consumer;

public sealed partial class SecondProjector(IAccountRepository target, IConsumerScope scope)
    : BatchProjector(target, ConsumerStreams.AccountsPattern, "second-projector"),
        IProjectorHandler<Deposited>, IProjectorHandler<Declined>
{
    public ValueTask<Result> HandleAsync(IProjectorContext<Declined> context, CancellationToken ct)
    {
        target.Add(context.Trigger.Metadata.AggregateId, 0, scope.Id);
        return ValueTask.FromResult(Result.Success);
    }

    public ValueTask<Result> HandleAsync(IProjectorContext<Deposited> context, CancellationToken ct)
    {
        target.Add(context.Trigger.Metadata.AggregateId, context.Trigger.Amount, scope.Id);
        return ValueTask.FromResult(Result.Success);
    }
}

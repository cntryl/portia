namespace Cntryl.Portia.Consumer;

public sealed partial class SecondReactor(
    IConsumerEffects effects,
    IConsumerScope scope,
    IProjectionCheckpointStore checkpoints)
    : Reactor(checkpoints, ConsumerStreams.AccountsPattern, "second-reactor"),
        IReactorHandler<Deposited>, IReactorHandler<Declined>
{
    public ValueTask<Result> HandleAsync(IReactorContext<Declined> context, CancellationToken ct)
    {
        effects.Record(Name, context.Trigger.Metadata.AggregateId, 0, scope.Id);
        return ValueTask.FromResult(Result.Success);
    }

    public ValueTask<Result> HandleAsync(IReactorContext<Deposited> context, CancellationToken ct)
    {
        effects.Record(Name, context.Trigger.Metadata.AggregateId, context.Trigger.Amount, scope.Id);
        return ValueTask.FromResult(Result.Success);
    }
}

namespace Cntryl.Portia.Consumer;

public sealed partial class FirstReactor(IConsumerEffects effects, IConsumerScope scope, IProjectionCheckpointStore checkpoints)
    : Reactor(checkpoints, EventStreamPattern.ForPattern("consumer", "accounts"), "first-reactor"), IReactorHandler<Deposited>, IReactorHandler<Declined>
{
    public ValueTask HandleAsync(IReactorContext<Declined> context, CancellationToken ct)
    {
        effects.Record(Name, context.Trigger.Metadata.AggregateId, 0, scope.Id);
        return ValueTask.CompletedTask;
    }

    public ValueTask HandleAsync(IReactorContext<Deposited> context, CancellationToken ct)
    {
        effects.Record(Name, context.Trigger.Metadata.AggregateId, context.Trigger.Amount, scope.Id);
        return ValueTask.CompletedTask;
    }
}

public sealed partial class FirstProjector(IAccountRepository target, IConsumerScope scope)
    : BatchProjector(target, EventStreamPattern.ForPattern("consumer", "accounts"), "first-projector"),
        IProjectorHandler<Deposited>, IProjectorHandler<Declined>
{
    public ValueTask HandleAsync(Declined ev, IProjectorContext context, CancellationToken ct)
    {
        target.Add(ev.Metadata.AggregateId, 0, scope.Id);
        return ValueTask.CompletedTask;
    }

    public ValueTask HandleAsync(Deposited ev, IProjectorContext context, CancellationToken ct)
    {
        target.Add(ev.Metadata.AggregateId, ev.Amount, scope.Id);
        return ValueTask.CompletedTask;
    }
}

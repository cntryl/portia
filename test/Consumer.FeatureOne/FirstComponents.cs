namespace Cntryl.Portia.Consumer;

public sealed partial class FirstReactor(IConsumerEffects effects, IConsumerScope scope)
    : Reactor("first-reactor", EventStreamPattern.ForPattern("consumer", "accounts")), IReactorHandler<Deposited>
{
    public ValueTask HandleAsync(IReactorContext<Deposited> context, CancellationToken ct)
    {
        effects.Record(Name, context.Ev.Metadata.AggregateId, context.Ev.Amount, scope.Id);
        return ValueTask.CompletedTask;
    }
}

public sealed partial class FirstProjector(IProjectionTarget<IAccountProjection> target, IConsumerScope scope)
    : Projector<IAccountProjection>("first-projector", EventStreamPattern.ForPattern("consumer", "accounts"), target),
        IProjectorHandler<Deposited, IAccountProjection>
{
    public ValueTask HandleAsync(Deposited ev, IProjectorContext<IAccountProjection> context, CancellationToken ct)
    {
        context.Projection.Add(ev.Metadata.AggregateId, ev.Amount, scope.Id);
        return ValueTask.CompletedTask;
    }
}

namespace Cntryl.Portia.Consumer;

public sealed partial class SecondProjector(IAccountRepository target, IConsumerScope scope)
    : BatchProjector(target, EventStreamPattern.ForPattern("consumer", "accounts"), "second-projector"),
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

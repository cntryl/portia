namespace Cntryl.Portia.Consumer;

public partial class FirstProjector(IAccountRepository target, IConsumerScope scope, EventStreamPattern? pattern = null)
    : BatchProjector(target, pattern ?? ConsumerStreams.AccountsPattern, "first-projector"),
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

public sealed partial class TenantFirstProjector(IAccountRepository target, IConsumerScope scope)
    : FirstProjector(target, scope, EventStreamPattern.ForTenant(ConsumerStreams.Accounts));

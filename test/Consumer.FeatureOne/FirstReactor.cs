namespace Cntryl.Portia.Consumer;

public partial class FirstReactor(
    IConsumerEffects effects,
    IConsumerScope scope,
    IProjectionCheckpointStore checkpoints,
    EventStreamPattern? pattern = null)
    : Reactor(checkpoints, pattern ?? ConsumerStreams.AccountsPattern, "first-reactor"),
        IReactorHandler<Deposited>, IReactorHandler<Declined>
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

public sealed partial class TenantFirstReactor(
    IConsumerEffects effects,
    IConsumerScope scope,
    IProjectionCheckpointStore checkpoints)
    : FirstReactor(effects, scope, checkpoints, EventStreamPattern.ForTenant(ConsumerStreams.Accounts));

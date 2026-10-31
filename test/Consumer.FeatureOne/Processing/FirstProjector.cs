namespace Cntryl.Portia.Consumer;

public partial class FirstProjector(IAccountRepository target, IConsumerScope scope, EventStreamPattern? pattern = null)
    : BatchProjector(target, pattern ?? ConsumerStreams.AccountsPattern, "first-projector"),
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

public sealed partial class TenantFirstProjector(IAccountRepository target, IConsumerScope scope)
    : FirstProjector(target, scope, EventStreamPattern.ForTenant(ConsumerStreams.Accounts));

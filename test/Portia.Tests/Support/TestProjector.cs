namespace Cntryl.Portia;

sealed partial class TestProjector(ITestProjectionRepository target, EventStreamPattern? pattern = null)
    : BatchProjector(target, pattern ?? EventStreamPattern.ForPattern("test", "projectors"), "test-projector"),
        IProjectorHandler<ValueChanged>,
        IProjectorHandler<ValueIncremented>
{
    public ValueTask<Result> HandleAsync(IProjectorContext<ValueChanged> context, CancellationToken ct)
    {
        target.Projection.Value = context.Trigger.Value;
        target.Projection.HandlerCount++;
        return ValueTask.FromResult(Result.Success);
    }

    public async ValueTask<Result> HandleAsync(IProjectorContext<ValueIncremented> context, CancellationToken ct)
    {
        await target.Projection.LoadAsync(ct);
        target.Projection.Value += context.Trigger.Amount;
        target.Projection.HandlerCount++;

        return Result.Success;
    }
}

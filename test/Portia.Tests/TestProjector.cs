namespace Cntryl.Portia;

sealed partial class TestProjector(ITestProjectionRepository target)
    : BatchProjector(target, EventStreamPattern.ForPattern("test", "projectors"), "test-projector"),
        IProjectorHandler<ValueChanged>,
        IProjectorHandler<ValueIncremented>
{
    public ValueTask HandleAsync(ValueChanged ev, IProjectorContext context, CancellationToken ct)
    {
        target.Projection.Value = ev.Value;
        target.Projection.HandlerCount++;
        return ValueTask.CompletedTask;
    }

    public async ValueTask HandleAsync(ValueIncremented ev, IProjectorContext context, CancellationToken ct)
    {
        await target.Projection.LoadAsync(ct);
        target.Projection.Value += ev.Amount;
        target.Projection.HandlerCount++;
    }
}

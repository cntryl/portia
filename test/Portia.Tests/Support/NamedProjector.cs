namespace Cntryl.Portia;

sealed partial class NamedProjector(RecordingProjectionTarget target)
    : Projector(target, EventStreamPattern.ForPattern("test", "projectors"), "declared-projection-name"),
        IProjectorHandler<ValueChanged>
{
    public ValueTask<Result> HandleAsync(IProjectorContext<ValueChanged> context, CancellationToken ct)
    {
        target.Projection.Value = context.Trigger.Value;
        return ValueTask.FromResult(Result.Success);
    }
}

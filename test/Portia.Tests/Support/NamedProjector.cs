namespace Cntryl.Portia;

sealed partial class NamedProjector(RecordingProjectionTarget target)
    : Projector(target, EventStreamPattern.ForPattern("test", "projectors"), "declared-projection-name"),
        IProjectorHandler<ValueChanged>
{
    public ValueTask HandleAsync(ValueChanged ev, IProjectorContext context, CancellationToken ct)
    {
        target.Projection.Value = ev.Value;
        return ValueTask.CompletedTask;
    }
}

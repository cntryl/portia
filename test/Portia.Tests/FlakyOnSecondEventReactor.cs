namespace Cntryl.Portia;

sealed partial class FlakyOnSecondEventReactor(IProjectionCheckpointStore? checkpoints = null)
    : BatchReactor(checkpoints ?? new InMemoryProjectionCheckpointStore(),
            EventStreamPattern.ForPattern("test", "reactors"), "flaky-on-second-event-reactor"),
        IReactorHandler<ValueChanged>
{
    bool _hasFailedOnce;


    public List<int> HandledValues { get; } = [];

    public ValueTask HandleAsync(IReactorContext<ValueChanged> context, CancellationToken ct)
    {
        if (context.Trigger.Value == 2 && !_hasFailedOnce)
        {
            _hasFailedOnce = true;
            throw new InvalidOperationException("Simulated transient failure reacting to the second event.");
        }
        else
        {
            HandledValues.Add(context.Trigger.Value);
            return ValueTask.CompletedTask;
        }
    }
}

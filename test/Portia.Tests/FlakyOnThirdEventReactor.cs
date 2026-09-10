namespace Cntryl.Portia;

sealed partial class FlakyOnThirdEventReactor(IProjectionCheckpointStore? checkpoints = null)
    : BatchReactor(checkpoints ?? new InMemoryProjectionCheckpointStore(),
            EventStreamPattern.ForPattern("test", "reactors"), "flaky-on-third-event-reactor"),
        IReactorHandler<ValueChanged>
{
    bool _hasFailedOnce;


    public List<int> HandledValues { get; } = [];

    public ValueTask HandleAsync(IReactorContext<ValueChanged> context, CancellationToken ct)
    {
        if (context.Trigger.Value == 3 && !_hasFailedOnce)
        {
            _hasFailedOnce = true;
            throw new InvalidOperationException("Simulated transient failure reacting to the third event.");
        }
        else
        {
            HandledValues.Add(context.Trigger.Value);
            return ValueTask.CompletedTask;
        }
    }
}

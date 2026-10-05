using Cntryl.Portia;
using Cntryl.Portia.Testing;

// A runnable example that is also compiled against freshly packed packages by CI.
static class ManualBoundedPassExample
{
    public static async Task RunAsync()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("manual", "events", id.ToString());
        var events = Enumerable.Range(1, 4097).Select(value =>
            DomainEventSeed.Attach(new Observed(value), id, (ulong)value)).ToArray();
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, events);
        var projector = new CountingProjector();
        var projectionRunner = new ProjectorRunner(store);
        var first = await projectionRunner.RunAsync(projector, ProjectionCheckpoint.Start);
        Require(projector.Last == 4096, "First projection pass must stop at its source-record budget.");
        var second = await projectionRunner.RunAsync(projector, first);
        Require(projector.Last == 4097 && second != first, "Resume must observe the final event.");
        Require(await projectionRunner.RunAsync(projector, second) == second, "Empty pass must not advance.");

        var reactor = new CountingReactor();
        var reactionRunner = new ReactorRunner(store);
        first = await reactionRunner.RunAsync(reactor, ProjectionCheckpoint.Start, maxBatchSize: 128);
        Require(reactor.Last == 4096, "Commit batch size must not redefine the pass budget.");
        second = await reactionRunner.RunAsync(reactor, first, maxBatchSize: 128);
        Require(reactor.Last == 4097 && second != first, "Reactor must resume from returned progress.");
        Require(await reactionRunner.RunAsync(reactor, second) == second, "Empty reaction pass must not advance.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await RequireCanceledAsync(() => projectionRunner.RunAsync(projector, ProjectionCheckpoint.Start,
            ct: cancellation.Token).AsTask());
        await RequireCanceledAsync(() => reactionRunner.RunAsync(reactor, ProjectionCheckpoint.Start,
            ct: cancellation.Token).AsTask());
        Require(projector.Last == 4097 && reactor.Last == 4097, "Canceled passes must not execute more handlers.");
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    static async Task RequireCanceledAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Canceled manual pass did not propagate cancellation.");
    }

    sealed record Observed(int Value) : DomainEvent;

    sealed class CountingProjector() : BatchProjector(new UserlandSession(new UserlandTarget()), EventStreamPattern.ForPattern("manual", "events"))
    {
        public int Last { get; private set; }
        protected override ValueTask ProjectBatchAsync(IReadOnlyList<DomainEventRecord> records, IProjectorContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Last = ((Observed)records[^1].Event).Value;
            return ValueTask.CompletedTask;
        }
    }

    sealed class CountingReactor() : BatchReactor(new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("manual", "events"))
    {
        public int Last { get; private set; }
        protected override ValueTask ReactBatchAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Last = ((Observed)contexts[^1].Source.Event).Value;
            return ValueTask.CompletedTask;
        }
    }
}

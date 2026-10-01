using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

// A synchronous allocation control, independent of clocks, exporters and example applications.
static class ProcessorPassAllocationProbe
{
    internal static void Run()
    {
        const int iterations = 16;
        Console.WriteLine("processor,events,max_batch,pass_budget,iterations,bytes_per_pass");
        foreach (var count in new[] { 0, 1 })
        {
            var store = new InMemoryEventStore();
            var pattern = EventStreamPattern.ForPattern("allocation", "events");
            if (count != 0)
            {
                var id = Uuid.CreateVersion4();
                var append = store.AppendAsync(new EventStreamAddress("allocation", "events", "one"), 0,
                    [DomainEventSeed.Attach(new ConformanceEvent(1), id, 1)]);
                if (!append.IsCompletedSuccessfully)
                    throw new InvalidOperationException("Allocation probe requires synchronous fixture seeding.");
                append.GetAwaiter().GetResult();
            }
            foreach (var size in new[] { 1, 1_048_576 })
            {
                var options = new ProjectionRunOptions { MaxBatchSize = size, MaxEventsPerPass = 1 };
                var projector = new ProbeProjector(new ProjectorScenario().Store, pattern);
                var reactor = new ProbeReactor(new InMemoryProjectionCheckpointStore(), pattern);
                var projectors = new ProjectorRunner(store);
                var reactors = new ReactorRunner(store);
                var projectionBytes = Measure(() => projectors.RunAsync(projector, ProjectionCheckpoint.Start, options));
                var reactionBytes = Measure(() => reactors.RunPassAsync(reactor, ProjectionCheckpoint.Start, options));
                if (projector.Processed != count * (iterations + 2) || reactor.Processed != count * (iterations + 2))
                    throw new InvalidOperationException("Allocation probe did not process exactly its bounded source.");
                Console.WriteLine(FormattableString.Invariant($"projector,{count},{size},1,{iterations},{projectionBytes}"));
                Console.WriteLine(FormattableString.Invariant($"reactor,{count},{size},1,{iterations},{reactionBytes}"));
            }
        }

        static long Measure(Func<ValueTask<ProjectionCheckpoint>> operation)
        {
            for (var index = 0; index < 2; index++)
                Invoke();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < iterations; index++)
                Invoke();
            return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

            void Invoke()
            {
                var pending = operation();
                if (!pending.IsCompletedSuccessfully)
                    throw new InvalidOperationException("Allocation probe must complete on the measuring thread.");
                _ = pending.Result;
            }
        }
    }

    sealed class ProbeProjector(IProjectionStore store, EventStreamPattern pattern) : BatchProjector(store, pattern)
    {
        public int Processed { get; private set; }
        protected override ValueTask ProjectBatchAsync(IReadOnlyList<DomainEventRecord> records,
            IProjectorContext context, CancellationToken ct)
        {
            Processed += records.Count;
            return ValueTask.CompletedTask;
        }
    }

    sealed class ProbeReactor(IProjectionCheckpointStore store, EventStreamPattern pattern) : BatchReactor(store, pattern)
    {
        public int Processed { get; private set; }
        protected override ValueTask ReactBatchAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct)
        {
            Processed += contexts.Count;
            return ValueTask.CompletedTask;
        }
    }
}

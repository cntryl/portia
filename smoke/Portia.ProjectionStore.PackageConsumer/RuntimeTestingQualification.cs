using System.Text.Json;
using Cntryl.Portia;
using Cntryl.Portia.Testing;
using Microsoft.Extensions.DependencyInjection;

static class RuntimeTestingQualification
{
    internal static async Task RunAsync()
    {
        if (JsonSerializer.IsReflectionEnabledByDefault)
            throw new InvalidOperationException("JSON reflection must be disabled for this consumer.");
        var fieldRejected = false;
        try
        {
            EventAssert.Equal(new FieldPayload(1) { Count = 2 }, new FieldPayload(1) { Count = 3 });
        }
        catch (InvalidOperationException error) when (error.Message.Contains("Count", StringComparison.Ordinal))
        {
            fieldRejected = true;
        }
        if (!fieldRejected)
            throw new InvalidOperationException("A public payload field mismatch was missed.");

        var id = Uuid.CreateVersion4();
        var failure = new InvalidOperationException("Replay failed after mutation.");
        var damaged = new ReplayProbeAggregate(id, failure);
        var replayRejected = false;
        try
        {
            _ = new AggregateScenario<ReplayProbeAggregate>(damaged).Given(new ConformanceEvent(1));
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { replayRejected = true; }
        if (!replayRejected)
            throw new InvalidOperationException("Replay did not preserve its original exception.");
        if (damaged.Applied != 1)
            throw new InvalidOperationException("Replay mutation was not exercised.");
        await ExpectDiscardAsync(() => { damaged.Emit(); return Task.CompletedTask; });
        await ExpectDiscardAsync(() => { damaged.Audit(); return Task.CompletedTask; });
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEventStore>(new InMemoryEventStore());
        _ = services.AddPortia();
        await using (var provider = services.BuildServiceProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            await ExpectDiscardAsync(() => scope.ServiceProvider.GetRequiredService<IAggregateReader>()
                .HydrateAsync(damaged).AsTask());
            await ExpectDiscardAsync(() => scope.ServiceProvider.GetRequiredService<IAggregateWriter>()
                .SaveAsync(damaged, new RequestDispatchContext(RequestActor.System)).AsTask());
        }
        var fresh = new ReplayProbeAggregate(id);
        _ = new AggregateScenario<ReplayProbeAggregate>(fresh).Given(new ConformanceEvent(1));
        if (fresh.Applied != 1 || fresh.Version != 1)
            throw new InvalidOperationException("Fresh aggregate recovery failed.");

        var events = Enumerable.Range(1, 4097).Select(index => (DomainEvent)new ConformanceEvent((ulong)index)).ToArray();
        var projection = new ProjectorScenario().Given(events);
        var projector = new ProbeProjector(projection.Store);
        await projection.RunAsync(projector);
        await projection.RunAsync(projector);
        if (projector.Count != 4097)
            throw new InvalidOperationException("Projector scenario did not drain/resume its history.");
        var reactions = new ReactorScenario().Given(events);
        var reactor = new ProbeReactor(new InMemoryProjectionCheckpointStore());
        await reactions.RunAsync(reactor);
        await reactions.RunAsync(reactor);
        if (reactor.Count != 8194)
            throw new InvalidOperationException("Reactor scenario did not replay its full history per run.");

        var store = new InMemoryEventStore();
        await store.AppendAsync(new EventStreamAddress("package", "bounded", "one"), 0, [events[0]]);
        var bounded = new ProbeProjector(new ProjectorScenario().Store, EventStreamPattern.ForPattern("package", "bounded"));
        var next = await new ProjectorRunner(store).RunAsync(bounded, ProjectionCheckpoint.Start,
            new ProjectionRunOptions { MaxBatchSize = 1_048_576, MaxEventsPerPass = 1 });
        if (bounded.Count != 1 || next.Cursor == EventCursor.Start)
            throw new InvalidOperationException("Bounded partial batch did not commit.");
        await EventStoreConformance.VerifyAsync(new StoreProbe());
    }

    static async Task ExpectDiscardAsync(Func<Task> operation)
    {
        try
        { await operation(); }
        catch (InvalidOperationException error) when (error.Message.Contains("discarded", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("A damaged aggregate operation was accepted.");
    }

    sealed record FieldPayload(int Id) : DomainEvent { public int Count; }

    sealed class ReplayProbeAggregate : Aggregate
    {
        public ReplayProbeAggregate(Uuid id, Exception? failure = null)
            : base(id, new EventStreamAddress("package", "replay", id.ToString()))
        {
            On<ConformanceEvent>(_ => { Applied++; if (failure is not null) throw failure; });
        }
        public int Applied { get; private set; }
        public void Emit() => RaiseEvent(new ConformanceEvent(1));
        public void Audit() => AuditEvent(new ConformanceEvent(0));
    }

    sealed class ProbeProjector(IProjectionStore store, EventStreamPattern? pattern = null)
        : BatchProjector(store, pattern ?? EventStreamPattern.ForPattern("package", "scenario"))
    {
        public int Count { get; private set; }
        protected override ValueTask ProjectBatchAsync(IReadOnlyList<DomainEventRecord> records, IProjectorContext context, CancellationToken ct)
        { Count += records.Count; return ValueTask.CompletedTask; }
    }

    sealed class ProbeReactor(IProjectionCheckpointStore store)
        : BatchReactor(store, EventStreamPattern.ForPattern("package", "scenario"))
    {
        public int Count { get; private set; }
        protected override ValueTask ReactBatchAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct)
        { Count += contexts.Count; return ValueTask.CompletedTask; }
    }

    sealed class StoreProbe : IEventStoreConformanceProbe
    {
        InMemoryEventStore _store = new();
        public string Realm => "package-conformance";
        public string Area => "events";
        public ValueTask ResetAsync(CancellationToken ct = default) { _store = new(); return ValueTask.CompletedTask; }
        public ValueTask<IEventStore> OpenAsync(CancellationToken ct = default) => ValueTask.FromResult<IEventStore>(_store);
    }
}

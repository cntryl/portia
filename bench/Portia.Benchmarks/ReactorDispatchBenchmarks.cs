using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

/// <summary>Measures reactor runner overhead over an in-memory event source and checkpoint store.</summary>
[MemoryDiagnoser]
public class ReactorDispatchBenchmarks
{
    readonly BenchmarkCheckpointStore _checkpoints = new();
    readonly BenchmarkReader _reader = new();
    BatchBenchmarkReactor _batch = null!;
    ReactorRunner _runner = null!;
    SingleBenchmarkReactor _single = null!;

    /// <summary>Gets or sets the number of events processed by each invocation.</summary>
    [Params(1, 32, 512)]
    public int EventCount { get; set; }

    /// <summary>Creates stable, fully identified source records.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var aggregateId = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("bench", "events", aggregateId.ToString());
        _reader.Records = Enumerable.Range(0, EventCount).Select(index =>
        {
            var ev = DomainEventSeed.Attach(new ProcessorBenchmarkEvent(index), aggregateId, (ulong)index + 1,
                occurredOn: DateTimeOffset.UnixEpoch);
            return new DomainEventRecord(stream, ev, (ulong)index, (ulong)index, (ulong)index);
        }).ToArray();
        _runner = new ReactorRunner(_reader);
        _single = new SingleBenchmarkReactor(_checkpoints);
        _batch = new BatchBenchmarkReactor(_checkpoints);
    }

    /// <summary>Reacts to and checkpoints every event individually.</summary>
    [Benchmark(Baseline = true)]
    public ValueTask<ProjectionCheckpoint> SingleEvent() =>
        _runner.RunPassAsync(_single, ProjectionCheckpoint.Start, ProjectionRunOptions.Default);

    /// <summary>Reacts to all available events and checkpoints once.</summary>
    [Benchmark]
    public ValueTask<ProjectionCheckpoint> Batch() => _runner.RunPassAsync(_batch, ProjectionCheckpoint.Start,
        new ProjectionRunOptions { MaxBatchSize = EventCount });

    sealed class BenchmarkReader : IDomainEventReader
    {
        public DomainEventRecord[] Records { get; set; } = [];

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0,
            CancellationToken ct = default) => Read(ct);

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong fromOffset = 0,
            CancellationToken ct = default) => Read(ct);

        async IAsyncEnumerable<DomainEventRecord> Read([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var record in Records)
            {
                ct.ThrowIfCancellationRequested();
                yield return record;
            }

            await Task.CompletedTask;
        }
    }

    sealed class BenchmarkCheckpointStore : IProjectionCheckpointStore
    {
        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity,
            CancellationToken ct = default) => ValueTask.FromResult(ProjectionCheckpoint.Start);

        public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint,
            CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    sealed class SingleBenchmarkReactor(IProjectionCheckpointStore checkpoints)
        : Reactor(checkpoints, EventStreamPattern.ForPattern("bench"))
    {
        protected override ValueTask ReactToEventAsync(DomainEventRecord record, IExecutionContext context,
            CancellationToken ct) => ValueTask.CompletedTask;
    }

    sealed class BatchBenchmarkReactor(IProjectionCheckpointStore checkpoints)
        : BatchReactor(checkpoints, EventStreamPattern.ForPattern("bench"))
    {
        protected override ValueTask ReactBatchAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }
}

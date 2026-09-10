using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

/// <summary>Measures runner and generated-dispatch overhead over an in-memory event source.</summary>
[MemoryDiagnoser]
public class ProcessorDispatchBenchmarks : IDisposable
{
    readonly BenchmarkProjectionStore _store = new();
    readonly BenchmarkReader _reader = new();
    BatchBenchmarkProjector _batch = null!;
    ProjectorRunner _runner = null!;
    SingleBenchmarkProjector _single = null!;

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
        _runner = new ProjectorRunner(_reader);
        _single = new SingleBenchmarkProjector(_store);
        _batch = new BatchBenchmarkProjector(_store);
    }

    /// <summary>Processes and commits every event individually.</summary>
    [Benchmark(Baseline = true)]
    public ValueTask<ProjectionCheckpoint> SingleEvent() => _runner.RunAsync(_single, ProjectionCheckpoint.Start);

    /// <summary>Processes all available events through one generated batch handler and commit.</summary>
    [Benchmark]
    public ValueTask<ProjectionCheckpoint> Batch() => _runner.RunAsync(_batch, ProjectionCheckpoint.Start,
        new ProjectionRunOptions { MaxBatchSize = EventCount });

    /// <inheritdoc />
    public void Dispose()
    {
        _store.Dispose();
        GC.SuppressFinalize(this);
    }

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

    sealed class BenchmarkProjectionStore : IProjectionStore, IProjectionBatch, IDisposable
    {
        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity,
            CancellationToken ct = default) => ValueTask.FromResult(ProjectionCheckpoint.Start);

        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context,
            CancellationToken ct = default) => ValueTask.FromResult<IProjectionBatch>(this);

        public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Dispose() => GC.SuppressFinalize(this);
    }
}

[Discriminator("benchmark.processor")]
sealed record ProcessorBenchmarkEvent(int Value) : DomainEvent;

sealed partial class SingleBenchmarkProjector(IProjectionStore store)
    : Projector(store, EventStreamPattern.ForPattern("bench")), IProjectorHandler<ProcessorBenchmarkEvent>
{
    public ValueTask HandleAsync(ProcessorBenchmarkEvent ev, IProjectorContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;
}

sealed partial class BatchBenchmarkProjector(IProjectionStore store)
    : BatchProjector(store, EventStreamPattern.ForPattern("bench")), IBatchProjectorHandler<ProcessorBenchmarkEvent>
{
    public ValueTask HandleAsync(IReadOnlyList<ProcessorBenchmarkEvent> events, IProjectorContext context,
        CancellationToken ct) => ValueTask.CompletedTask;
}

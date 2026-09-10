using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

/// <summary>Measures aggregate hydration and its invariant validation over an in-memory history.</summary>
[MemoryDiagnoser]
public class AggregateRepositoryBenchmarks
{
    Uuid _aggregateId;
    AggregateRepository _repository = null!;
    BenchmarkEventStore _store = null!;
    EventStreamAddress _stream = null!;

    /// <summary>Gets or sets the aggregate history length.</summary>
    [Params(0, 32, 512)]
    public int EventCount { get; set; }

    /// <summary>Creates a stable valid history.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _aggregateId = Uuid.CreateVersion4();
        _stream = new EventStreamAddress("bench", "aggregates", _aggregateId.ToString());
        _store = new BenchmarkEventStore
        {
            Records = Enumerable.Range(0, EventCount).Select(index =>
            {
                var ev = DomainEventSeed.Attach(new ProcessorBenchmarkEvent(index), _aggregateId,
                    (ulong)index + 1, occurredOn: DateTimeOffset.UnixEpoch);
                return new DomainEventRecord(_stream, ev, (ulong)index, null, null);
            }).ToArray()
        };
        _repository = new AggregateRepository(_store);
    }

    /// <summary>Constructs and hydrates one aggregate from its complete history.</summary>
    [Benchmark]
    public ValueTask<BenchmarkAggregate> Hydrate() =>
        _repository.HydrateAsync(new BenchmarkAggregate(_aggregateId, _stream));

    sealed class BenchmarkEventStore : IEventStore
    {
        public DomainEventRecord[] Records { get; set; } = [];

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0,
            CancellationToken ct = default) => Read(fromOffset, ct);

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong fromOffset = 0,
            CancellationToken ct = default) => Read(fromOffset, ct);

        public ValueTask AppendAsync(EventStreamAddress stream, ulong expectedStreamPosition,
            IReadOnlyList<DomainEvent> events, CancellationToken ct = default) => ValueTask.CompletedTask;

        async IAsyncEnumerable<DomainEventRecord> Read(ulong fromOffset,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var record in Records)
            {
                ct.ThrowIfCancellationRequested();
                if (record.ResourceOffset >= fromOffset)
                    yield return record;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>Minimal aggregate used to apply the benchmark event history.</summary>
    public sealed class BenchmarkAggregate : Aggregate
    {
        /// <summary>Creates an empty aggregate at the selected stream.</summary>
        public BenchmarkAggregate(Uuid id, EventStreamAddress stream) : base(id, stream) =>
            On<ProcessorBenchmarkEvent>(_ => { });
    }
}

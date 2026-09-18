using System.Globalization;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Cntryl.Portia.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Measures aggregate hydration and its invariant validation over an in-memory history.</summary>
[MemoryDiagnoser]
public class AggregateRepositoryBenchmarks
{
    Uuid _aggregateId;
    IAggregateReader _reader = null!;
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
                return new DomainEventRecord(_stream, ev, (ulong)index,
                    new EventCursor((index + 1).ToString(CultureInfo.InvariantCulture)));
            }).ToArray()
        };
        _reader = Reader(_store);
    }

    /// <summary>Constructs and hydrates one aggregate from its complete history.</summary>
    [Benchmark]
    public ValueTask<BenchmarkAggregate> Hydrate() =>
        _reader.HydrateAsync(new BenchmarkAggregate(_aggregateId, _stream));

    // Benchmarks compose the reader exactly as an application does, through AddPortia.
    internal static IAggregateReader Reader(IEventStore store)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton(store);
        _ = services.AddPortia();
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IAggregateReader>();
    }

    internal sealed class BenchmarkEventStore : IEventStore
    {
        public DomainEventRecord[] Records { get; set; } = [];

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset,
            CancellationToken ct) => Read(fromOffset, ct);

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor,
            CancellationToken ct) =>
            Read(cursor == EventCursor.Start ? 0 : ulong.Parse(cursor.Value!, CultureInfo.InvariantCulture), ct);

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
        public BenchmarkAggregate(Uuid id, EventStreamAddress stream) : base(id, stream)
        {
            On<ProcessorBenchmarkEvent>(_ => { });
        }
    }
}

/// <summary>Measures bounded, one-invocation aggregate hydration for large histories.</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, 1, 1, 3, 1)]
public class LargeAggregateRepositoryBenchmarks
{
    Uuid _aggregateId;
    IAggregateReader _reader = null!;
    EventStreamAddress _stream = null!;

    /// <summary>Gets or sets the large aggregate history length.</summary>
    [Params(10_000, 100_000)]
    public int EventCount { get; set; }

    /// <summary>Creates the complete history once, outside the measured invocation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _aggregateId = Uuid.CreateVersion4();
        _stream = new EventStreamAddress("bench", "large-aggregates", _aggregateId.ToString());
        var store = new AggregateRepositoryBenchmarks.BenchmarkEventStore
        {
            Records = Enumerable.Range(0, EventCount).Select(index =>
            {
                var ev = DomainEventSeed.Attach(new ProcessorBenchmarkEvent(index), _aggregateId,
                    (ulong)index + 1, occurredOn: DateTimeOffset.UnixEpoch);
                return new DomainEventRecord(_stream, ev, (ulong)index,
                    new EventCursor((index + 1).ToString(CultureInfo.InvariantCulture)));
            }).ToArray()
        };
        _reader = AggregateRepositoryBenchmarks.Reader(store);
    }

    /// <summary>Constructs and hydrates one aggregate from the configured large history.</summary>
    [Benchmark]
    public ValueTask<AggregateRepositoryBenchmarks.BenchmarkAggregate> Hydrate() =>
        _reader.HydrateAsync(new AggregateRepositoryBenchmarks.BenchmarkAggregate(_aggregateId, _stream));
}

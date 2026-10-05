using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

/// <summary>Measures mixed generated dispatch and real cursor advancement across bounded passes.</summary>
[MemoryDiagnoser]
public class MixedProcessorLifecycleBenchmarks : IDisposable
{
    MixedProcessorApplication _application = null!;

    /// <summary>Gets or sets an empty, partial, or over-budget source length.</summary>
    [Params(0, 33, 4097)]
    public int EventCount { get; set; }

    /// <summary>Gets or sets the generated single-event or batch dispatch boundary.</summary>
    [Params(1, 32, 128)]
    public int BatchSize { get; set; }

    /// <summary>Gets or sets whether each atomic commit genuinely yields.</summary>
    [Params(false, true)]
    public bool AsyncCommit { get; set; }

    /// <summary>Builds matched, derived and unmatched records and verifies checkpoint controls.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _application = new MixedProcessorApplication(EventCount, BatchSize, AsyncCommit);
        _application.QualifyAsync().GetAwaiter().GetResult();
    }

    /// <summary>Runs one pass with the default independent 4096-record budget.</summary>
    [Benchmark]
    public ValueTask<ProjectionCheckpoint> BoundedPass() => _application.RunAsync();

    /// <summary>Resumes from successive checkpoints until the entire fixed backlog is caught up.</summary>
    [Benchmark]
    public ValueTask<ProjectionCheckpoint> CatchUp() => _application.CatchUpAsync();

    /// <summary>Releases the bounded recording projection store.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _application?.Dispose();
        GC.SuppressFinalize(this);
    }
}

sealed class MixedProcessorApplication : IDisposable
{
    readonly MixedReader _reader;
    readonly MixedProjectionStore _store;
    readonly ProjectorRunner _runner;
    readonly Projector _projector;
    readonly ProjectionRunOptions _options;

    internal MixedProcessorApplication(int count, int batchSize, bool asyncCommit)
    {
        var id = Uuid.CreateVersion4();
        var address = new EventStreamAddress("bench", "mixed", id.ToString());
        _reader = new MixedReader(Enumerable.Range(0, count).Select(index =>
        {
            DomainEvent ev = (index % 3) switch
            {
                0 => new MixedMatched(index),
                1 => new MixedDerived(index),
                _ => new MixedUnmatched(index)
            };
            DomainEventSeed.Attach(ev, id, (ulong)index + 1, occurredOn: DateTimeOffset.UnixEpoch);
            return new DomainEventRecord(address, ev, (ulong)index,
                new EventCursor((index + 1).ToString(CultureInfo.InvariantCulture)));
        }).ToArray());
        _store = new MixedProjectionStore(asyncCommit);
        _runner = new ProjectorRunner(_reader);
        _projector = batchSize == 1 ? new MixedSingleProjector(_store) : new MixedBatchProjector(_store);
        _options = new ProjectionRunOptions { MaxBatchSize = batchSize, MaxEventsPerPass = 4096 };
    }

    internal ValueTask<ProjectionCheckpoint> RunAsync()
    {
        _store.Reset();
        _reader.ReadCount = 0;
        return _runner.RunAsync(_projector, ProjectionCheckpoint.Start, _options);
    }

    internal async ValueTask<ProjectionCheckpoint> CatchUpAsync()
    {
        _store.Reset();
        _reader.ReadCount = 0;
        var checkpoint = ProjectionCheckpoint.Start;
        while (true)
        {
            var next = await _runner.RunAsync(_projector, checkpoint, _options);
            if (next == checkpoint)
                return next;
            checkpoint = next;
        }
    }

    internal async Task QualifyAsync()
    {
        _store.RecordOrder = true;
        var pass = await RunAsync();
        var budget = Math.Min(4096, _reader.Records.Length);
        Verify(pass, budget);
        var all = await CatchUpAsync();
        Verify(all, _reader.Records.Length);
        var before = _store.Commits;
        if (await _runner.RunAsync(_projector, all, _options) != all || _store.Commits != before)
            throw new InvalidOperationException("An exhausted reader made progress.");
        _store.Reset();
        _store.FailCommit = true;
        if (_reader.Records.Length > 0)
        {
            try
            {
                await _runner.RunAsync(_projector, ProjectionCheckpoint.Start, _options);
                throw new InvalidOperationException("The failure control did not fail.");
            }
            catch (MixedCommitException) { }
            if (_store.Checkpoint != ProjectionCheckpoint.Start || _store.Commits != 0 || _store.Order.Count != 0)
                throw new InvalidOperationException("Failed commit leaked checkpoint or projected rows.");
        }
        _store.FailCommit = false;
        _reader.ReadCount = 0;
        var authoritative = await _store.LoadCheckpointAsync(new CheckpointIdentity(_projector.Name, _projector.Pattern));
        Verify(await _runner.RunAsync(_projector, authoritative, _options), Math.Min(4096, _reader.Records.Length));
        _store.Reset();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await _runner.RunAsync(_projector, ProjectionCheckpoint.Start, _options, canceled.Token);
            throw new InvalidOperationException("Projector cancellation control did not cancel.");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        if (_store.Checkpoint != ProjectionCheckpoint.Start || _store.Commits != 0 || _store.Order.Count != 0)
            throw new InvalidOperationException("Canceled projector changed data or progress.");
        _store.RecordOrder = false;
        _store.Reset();
    }

    void Verify(ProjectionCheckpoint checkpoint, int count)
    {
        var expected = count == 0 ? ProjectionCheckpoint.Start : new ProjectionCheckpoint(_reader.Records[count - 1].NextCursor);
        var order = Enumerable.Range(0, count).Where(index => index % 3 != 2);
        if (checkpoint != expected || _store.Checkpoint != expected || _reader.ReadCount != count || !_store.Order.SequenceEqual(order))
            throw new InvalidOperationException("Mixed dispatch skipped, reordered or repeated source records.");
        var size = _options.MaxBatchSize;
        if (_store.Commits != (count + size - 1) / size)
            throw new InvalidOperationException("Unexpected atomic commit count.");
    }

    public void Dispose() => _store.Dispose();
}

sealed class MixedReader(DomainEventRecord[] records) : IDomainEventReader
{
    internal DomainEventRecord[] Records => records;
    internal int ReadCount;
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset, CancellationToken ct) => Read((int)fromOffset, ct);
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor, CancellationToken ct) =>
        Read(cursor.Value is null ? 0 : int.Parse(cursor.Value, CultureInfo.InvariantCulture), ct);

    async IAsyncEnumerable<DomainEventRecord> Read(int start, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        for (var index = start; index < records.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            yield return records[index];
        }
        await Task.CompletedTask;
    }
}

sealed class MixedProjectionStore(bool asyncCommit) : IProjectionStore, IProjectionBatch, IDisposable
{
    readonly List<int> _pending = [];
    internal List<int> Order { get; } = [];
    internal bool RecordOrder { get; set; }
    internal bool FailCommit { get; set; }
    internal int Commits { get; private set; }
    internal ProjectionCheckpoint Checkpoint { get; private set; }
    internal void Stage(int value) { if (RecordOrder) _pending.Add(value); }
    internal void StageDerived(int value)
    {
        if (RecordOrder && value % 3 != 1)
            throw new InvalidOperationException("Derived dispatch selected the wrong handler.");
        Stage(value);
    }
    internal void StageBase(int value)
    {
        if (RecordOrder && value % 3 != 0)
            throw new InvalidOperationException("Base dispatch swallowed a derived handler.");
        Stage(value);
    }
    internal void Reset() { Commits = 0; Checkpoint = ProjectionCheckpoint.Start; Order.Clear(); _pending.Clear(); }
    public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default) => ValueTask.FromResult(Checkpoint);
    public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
    {
        if (context.Checkpoint != Checkpoint)
            throw new InvalidOperationException("Stale fixture checkpoint.");
        _pending.Clear();
        return ValueTask.FromResult<IProjectionBatch>(this);
    }
    public async ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
    {
        if (asyncCommit)
            await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (FailCommit)
            throw new MixedCommitException();
        if (RecordOrder)
            Order.AddRange(_pending);
        Checkpoint = checkpoint;
        Commits++;
    }
    public ValueTask DisposeAsync() { _pending.Clear(); return ValueTask.CompletedTask; }
    public void Dispose() => Reset();
}

sealed class MixedCommitException : Exception;
[Discriminator("benchmark.mixed.matched")]
record MixedMatched(int Value) : DomainEvent;
[Discriminator("benchmark.mixed.derived")]
sealed record MixedDerived(int Value) : MixedMatched(Value);
[Discriminator("benchmark.mixed.unmatched")]
sealed record MixedUnmatched(int Value) : DomainEvent;

sealed partial class MixedSingleProjector(MixedProjectionStore store)
    : Projector(store, EventStreamPattern.ForPattern("bench")), IProjectorHandler<MixedMatched>, IProjectorHandler<MixedDerived>
{
    public ValueTask HandleAsync(MixedMatched ev, IProjectorContext context, CancellationToken ct)
    { store.StageBase(ev.Value); return ValueTask.CompletedTask; }
    public ValueTask HandleAsync(MixedDerived ev, IProjectorContext context, CancellationToken ct)
    { store.StageDerived(ev.Value); return ValueTask.CompletedTask; }
}

sealed partial class MixedBatchProjector(MixedProjectionStore store)
    : BatchProjector(store, EventStreamPattern.ForPattern("bench")), IBatchProjectorHandler<MixedMatched>, IBatchProjectorHandler<MixedDerived>
{
    public ValueTask HandleAsync(IReadOnlyList<MixedMatched> events, IProjectorContext context, CancellationToken ct)
    {
        foreach (var ev in events)
            store.StageBase(ev.Value);
        return ValueTask.CompletedTask;
    }
    public ValueTask HandleAsync(IReadOnlyList<MixedDerived> events, IProjectorContext context, CancellationToken ct)
    {
        foreach (var ev in events)
            store.StageDerived(ev.Value);
        return ValueTask.CompletedTask;
    }
}

[PortiaJsonContext]
[JsonSerializable(typeof(MixedMatched))]
[JsonSerializable(typeof(MixedDerived))]
[JsonSerializable(typeof(MixedUnmatched))]
sealed partial class MixedProcessorJsonContext : JsonSerializerContext;

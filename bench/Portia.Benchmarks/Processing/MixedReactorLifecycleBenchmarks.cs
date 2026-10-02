using System.Globalization;
using BenchmarkDotNet.Attributes;
using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

/// <summary>Separates reactor checkpoint completion from a representative awaited effect.</summary>
[MemoryDiagnoser]
public class MixedReactorLifecycleBenchmarks
{
    MixedReactorApplication _application = null!;

    /// <summary>Gets or sets a partial batch or one-record-over-budget backlog.</summary>
    [Params(33, 4097)]
    public int EventCount { get; set; }

    /// <summary>Gets or sets single-event or batch reaction dispatch.</summary>
    [Params(1, 128)]
    public int BatchSize { get; set; }

    /// <summary>Gets or sets the independently measured asynchronous boundary.</summary>
    [Params("sync-checkpoint", "async-checkpoint", "awaited-effect")]
    public string Boundary { get; set; } = "sync-checkpoint";

    /// <summary>Verifies causality, effect identity, replay and authoritative checkpoints.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _application = new MixedReactorApplication(EventCount, BatchSize, Boundary);
        _application.QualifyAsync().GetAwaiter().GetResult();
    }

    /// <summary>Processes at most 4096 source records, including unmatched checkpoint progress.</summary>
    [Benchmark]
    public ValueTask<ProjectionCheckpoint> BoundedPass() => _application.RunAsync();

    /// <summary>Consumes the backlog using successive authoritative checkpoints.</summary>
    [Benchmark]
    public ValueTask<ProjectionCheckpoint> CatchUp() => _application.CatchUpAsync();
}

sealed class MixedReactorApplication
{
    readonly MixedReader _reader;
    readonly MixedReactionStore _store;
    readonly ReactorRunner _runner;
    readonly Reactor _reactor;
    readonly ProjectionRunOptions _options;

    internal MixedReactorApplication(int count, int batchSize, string boundary)
    {
        var id = Uuid.CreateVersion4();
        var address = new EventStreamAddress("bench", "reactions", id.ToString());
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
        _store = new MixedReactionStore(boundary == "async-checkpoint", boundary == "awaited-effect");
        _runner = new ReactorRunner(_reader);
        _reactor = batchSize == 1 ? new MixedSingleReactor(_store) : new MixedBatchReactor(_store);
        _options = new ProjectionRunOptions { MaxBatchSize = batchSize, MaxEventsPerPass = 4096 };
    }

    internal ValueTask<ProjectionCheckpoint> RunAsync()
    {
        _store.Reset();
        _reader.ReadCount = 0;
        return _runner.RunPassAsync(_reactor, ProjectionCheckpoint.Start, _options);
    }

    internal async ValueTask<ProjectionCheckpoint> CatchUpAsync()
    {
        _store.Reset();
        _reader.ReadCount = 0;
        var checkpoint = ProjectionCheckpoint.Start;
        while (true)
        {
            var next = await _runner.RunPassAsync(_reactor, checkpoint, _options);
            if (next == checkpoint)
                return next;
            checkpoint = next;
        }
    }

    internal async Task QualifyAsync()
    {
        _store.RecordControls = true;
        Verify(await RunAsync(), Math.Min(4096, _reader.Records.Length));
        var firstEffects = _store.Effects.ToArray();
        Verify(await RunAsync(), Math.Min(4096, _reader.Records.Length));
        if (!_store.Effects.SequenceEqual(firstEffects))
            throw new InvalidOperationException("Effect IDs changed on replay.");
        Verify(await CatchUpAsync(), _reader.Records.Length);
        _store.Reset();
        _store.FailCheckpoint = true;
        try
        {
            await _runner.RunPassAsync(_reactor, ProjectionCheckpoint.Start, _options);
            throw new InvalidOperationException("Checkpoint failure control did not fail.");
        }
        catch (MixedCommitException) { }
        if (_store.Checkpoint != ProjectionCheckpoint.Start || _store.Commits != 0 || _store.Effects.Count == 0)
            throw new InvalidOperationException("At-least-once effects/checkpoint failure semantics changed.");
        var failedEffects = _store.Effects.ToArray();
        _store.FailCheckpoint = false;
        _store.Order.Clear();
        _store.Effects.Clear();
        _reader.ReadCount = 0;
        var authoritative = await _store.LoadAsync(new CheckpointIdentity(_reactor.Name, _reactor.Pattern));
        Verify(await _runner.RunPassAsync(_reactor, authoritative, _options), Math.Min(4096, _reader.Records.Length));
        if (!_store.Effects.Take(failedEffects.Length).SequenceEqual(failedEffects))
            throw new InvalidOperationException("Failed-checkpoint replay changed stable effect identity.");
        _store.Reset();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await _runner.RunPassAsync(_reactor, ProjectionCheckpoint.Start, _options, canceled.Token);
            throw new InvalidOperationException("Cancellation control did not cancel.");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        _store.RecordControls = false;
        _store.Reset();
    }

    void Verify(ProjectionCheckpoint checkpoint, int count)
    {
        var expected = new ProjectionCheckpoint(_reader.Records[count - 1].NextCursor);
        var values = Enumerable.Range(0, count).Where(index => index % 3 != 2);
        if (checkpoint != expected || _store.Checkpoint != expected || _reader.ReadCount != count ||
            !_store.Order.SequenceEqual(values) || _store.Effects.Distinct().Count() != _store.Effects.Count ||
            _store.Commits != (count + _options.MaxBatchSize - 1) / _options.MaxBatchSize)
            throw new InvalidOperationException("Mixed reactor lost order, effects or authoritative progress.");
    }
}

sealed class MixedReactionStore(bool asyncCheckpoint, bool awaitedEffect) : IProjectionCheckpointStore
{
    internal bool RecordControls { get; set; }
    internal bool FailCheckpoint { get; set; }
    internal List<int> Order { get; } = [];
    internal List<Uuid> Effects { get; } = [];
    internal ProjectionCheckpoint Checkpoint { get; private set; }
    internal int Commits { get; private set; }
    internal void Reset() { Order.Clear(); Effects.Clear(); Checkpoint = ProjectionCheckpoint.Start; Commits = 0; }
    internal async ValueTask EffectAsync(IReactorContext context, Uuid effect, int value, bool derived, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (awaitedEffect)
            await Task.Yield();
        if (!RecordControls)
            return;
        if (context.CauseId != context.Source.Event.Metadata.EventId || !RequestActor.IsSystem(context.Actor) ||
            effect == Uuid.Empty || derived != (value % 3 == 1))
            throw new InvalidOperationException("Reaction lost causal/system context or derived handler selection.");
        Order.Add(value);
        Effects.Add(effect);
    }
    public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default) => ValueTask.FromResult(Checkpoint);
    public async ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default)
    {
        if (asyncCheckpoint)
            await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (FailCheckpoint)
            throw new MixedCommitException();
        Checkpoint = checkpoint;
        Commits++;
    }
}

sealed partial class MixedSingleReactor(MixedReactionStore store)
    : Reactor(store, EventStreamPattern.ForPattern("bench")), IReactorHandler<MixedMatched>, IReactorHandler<MixedDerived>
{
    public ValueTask HandleAsync(IReactorContext<MixedMatched> context, CancellationToken ct) =>
        store.EffectAsync(context, CreateEffectId(context, "benchmark-effect"), context.Trigger.Value, false, ct);
    public ValueTask HandleAsync(IReactorContext<MixedDerived> context, CancellationToken ct) =>
        store.EffectAsync(context, CreateEffectId(context, "benchmark-effect"), context.Trigger.Value, true, ct);
}

sealed partial class MixedBatchReactor(MixedReactionStore store)
    : BatchReactor(store, EventStreamPattern.ForPattern("bench")), IBatchReactorHandler<MixedMatched>, IBatchReactorHandler<MixedDerived>
{
    public async ValueTask HandleAsync(IReadOnlyList<IReactorContext<MixedMatched>> contexts, CancellationToken ct)
    {
        foreach (var context in contexts)
            await store.EffectAsync(context, CreateEffectId(context, "benchmark-effect"), context.Trigger.Value, false, ct);
    }
    public async ValueTask HandleAsync(IReadOnlyList<IReactorContext<MixedDerived>> contexts, CancellationToken ct)
    {
        foreach (var context in contexts)
            await store.EffectAsync(context, CreateEffectId(context, "benchmark-effect"), context.Trigger.Value, true, ct);
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Measures aggregate Raise and normal attributed Execute/Save with bounded stub persistence.</summary>
[MemoryDiagnoser]
public class AggregateLifecycleBenchmarks : IDisposable
{
    AggregateApplication _application = null!;

    /// <summary>Gets or sets the number of events emitted by one operation.</summary>
    [Params(0, 1, 8, 128)]
    public int Events { get; set; }

    /// <summary>Composes source-generated requests and validates explicit commit/discard decisions.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _application = new AggregateApplication();
        _application.QualifyAsync().GetAwaiter().GetResult();
    }

    /// <summary>Measures construction and synchronous raising without persistence.</summary>
    [Benchmark]
    public ulong RaiseOnly()
    {
        var aggregate = AggregateApplication.NewAggregate();
        aggregate.Change(Events);
        return aggregate.Version;
    }

    /// <summary>Measures ordinary request attribution, hydration, operation and one explicit commit.</summary>
    [Benchmark]
    public ValueTask<Result> ExecuteCommit() => _application.RunAsync(Events, "commit");

    /// <summary>Measures the caller's explicit discard, which persists no raised events.</summary>
    [Benchmark]
    public ValueTask<Result> ExecuteDiscard() => _application.RunAsync(Events, "discard");

    /// <summary>Measures ordinary attributed raising followed by direct Save.</summary>
    [Benchmark]
    public ValueTask<Result> RaiseThenSave() => _application.RunAsync(Events, "save");

    /// <summary>Disposes the reused scope and application.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _application?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Separates deterministic asynchronous stub persistence from completed backend overhead.</summary>
[MemoryDiagnoser]
public class AsyncAggregateLifecycleBenchmarks : IDisposable
{
    AggregateApplication _application = null!;

    /// <summary>Gets or sets the bounded atomic append size.</summary>
    [Params(8, 128)]
    public int Events { get; set; }

    /// <summary>Composes persistence that genuinely yields before completing one atomic append.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _application = new AggregateApplication(yieldPersistence: true);
        _application.QualifyAsync().GetAwaiter().GetResult();
    }

    /// <summary>Measures one asynchronously completed commit, including its declared scheduler hop.</summary>
    [Benchmark]
    public ValueTask<Result> ExecuteCommit() => _application.RunAsync(Events, "commit");

    /// <summary>Releases application resources.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _application?.Dispose();
        GC.SuppressFinalize(this);
    }
}

sealed class AggregateApplication : IDisposable
{
    static readonly Uuid AggregateId = Uuid.CreateVersion4();
    static readonly EventStreamAddress Stream = new("bench", "aggregate-lifecycle", AggregateId.ToString());
    readonly ServiceProvider _provider;
    readonly IServiceScope _scope;
    readonly AggregateRecordingStore _store;
    readonly IRequestBus _bus;

    internal AggregateApplication(bool yieldPersistence = false)
    {
        var services = new ServiceCollection();
        _store = new AggregateRecordingStore { YieldPersistence = yieldPersistence };
        services.AddSingleton<IEventStore>(_store);
        services.AddPortia().AddRequestHandler<AggregateEmitHandler>();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _provider.CreateScope();
        _bus = _scope.ServiceProvider.GetRequiredService<IRequestBus>();
    }

    internal static LifecycleAggregate NewAggregate() => new(AggregateId, Stream);
    internal ValueTask<Result> RunAsync(int count, string decision) => _bus.SendAsync(new AggregateEmit(count, decision), RuntimeApplication.Actor);

    internal async Task QualifyAsync()
    {
        foreach (var count in new[] { 0, 1, 8, 128 })
        {
            _store.Reset();
            if (!(await RunAsync(count, "commit")).IsSuccess || _store.Appends != (count == 0 ? 0 : 1) || _store.Last.Length != count)
                throw new InvalidOperationException("Aggregate fixture did not atomically commit the requested event count.");
            for (var index = 0; index < count; index++)
            {
                var ev = (LifecycleChanged)_store.Last[index];
                if (ev.Value != index || ev.Metadata.AggregateVersion != (ulong)index + 1 || ev.Metadata.EventId == Uuid.Empty)
                    throw new InvalidOperationException("Aggregate fixture lost event order or identity.");
            }
            if (_store.Last.Select(ev => ev.Metadata.EventId).Distinct().Count() != count)
                throw new InvalidOperationException("Aggregate fixture reused event IDs.");
            if (count > 0 && _store.Last.Any(ev => ev.Metadata.ExecutionId is null || ev.Metadata.CausationId is null))
                throw new InvalidOperationException("Ordinary request execution attribution was omitted.");
            _store.Reset();
            if ((await RunAsync(count, "discard")).IsSuccess || _store.Appends != 0)
                throw new InvalidOperationException("The operation's explicit discard/result was not respected.");
            _store.Reset();
            if (!(await RunAsync(count, "save")).IsSuccess || _store.Last.Length != count)
                throw new InvalidOperationException("Save did not preserve the raised event count.");
        }
        _store.Reset();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}

sealed class LifecycleAggregate : Aggregate
{
    internal LifecycleAggregate(Uuid id, EventStreamAddress stream) : base(id, stream) => On<LifecycleChanged>(_ => { });
    internal void Change(int count)
    {
        for (var index = 0; index < count; index++)
            RaiseEvent(new LifecycleChanged(index));
    }
}

[Discriminator("benchmark.lifecycle.changed", 1)]
sealed record LifecycleChanged(int Value) : DomainEvent;
sealed record AggregateEmit(int Count, string Decision) : IRequest;

sealed class AggregateEmitHandler(IAggregateExecutor executor, IAggregateWriter writer) : IRequestHandler<AggregateEmit>
{
    static readonly RequestError Discarded = new(RequestErrorKind.Conflict, "explicit fixture discard");
    public async ValueTask<Result> HandleAsync(IRequestContext<AggregateEmit> context, CancellationToken ct)
    {
        var aggregate = AggregateApplication.NewAggregate();
        if (context.Request.Decision == "save")
        {
            aggregate.Change(context.Request.Count);
            await writer.SaveAsync(aggregate, context, ct);
            return Result.Success;
        }
        return await executor.ExecuteAsync(aggregate, value =>
        {
            value.Change(context.Request.Count);
            return context.Request.Decision == "commit" ? AggregateOutcome.Commit(Result.Success) : AggregateOutcome.Discard(Result.Failure(Discarded));
        }, context, ct);
    }
}

sealed class AggregateRecordingStore : IEventStore
{
    internal bool YieldPersistence { get; init; }
    internal DomainEvent[] Last { get; private set; } = [];
    internal int Appends { get; private set; }
    internal void Reset() { Last = []; Appends = 0; }
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset, CancellationToken ct) => Empty(ct);
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor, CancellationToken ct) => Empty(ct);
    public async ValueTask AppendAsync(EventStreamAddress stream, ulong expectedStreamPosition, IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (expectedStreamPosition != 0)
            throw new EventStreamConcurrencyException("Fixture expects one fresh aggregate execution.");
        if (YieldPersistence)
            await Task.Yield();
        // Bounded last-append observation, never an accumulating durable-store simulation.
        Last = events.ToArray();
        Appends++;
    }
    static async IAsyncEnumerable<DomainEventRecord> Empty([EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}

[PortiaJsonContext]
[JsonSerializable(typeof(AggregateEmit))]
[JsonSerializable(typeof(LifecycleChanged))]
sealed partial class AggregateLifecycleJsonContext : JsonSerializerContext;

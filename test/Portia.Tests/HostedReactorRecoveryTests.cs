using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
/// Verifies the hosted reactor loop actually recovers correctly after a faulted
/// pass — not just that it doesn't crash the host, but that its retry starts from where the
/// pass genuinely got to, not from a stale, pre-pass checkpoint held only in this loop's own
/// local variable.
/// </summary>
public sealed class HostedReactorRecoveryTests
{
    /// <summary>
    /// Regression test: <c>ReactorRunner.RunAsync</c> can durably save one or more
    /// batches via its own <c>checkpointStore</c> parameter and then still fault partway through
    /// the next batch. The hosted service's own local <c>checkpoint</c> variable only advances on
    /// a successful <c>RunAsync</c> return, so without reloading from the store after a fault, a
    /// retry redoes every batch that was already durably saved — exactly the duplicate-side-effect
    /// risk the whole batched-checkpointing feature exists to bound.
    /// </summary>
    [Fact]
    public async Task ShouldResumeFromStoreCheckpointAfterFaultedPassRatherThanStaleLocalValue()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "reactors", id.ToString());
        var eventStore = new InMemoryEventStore();
        await eventStore.AppendAsync(stream, 0, [
            Committed(new ValueChanged(1), id, 1),
            Committed(new ValueChanged(2), id, 2),
            Committed(new ValueChanged(3), id, 3),
            Committed(new ValueChanged(4), id, 4),
        ]);
        var checkpointStore = new TransientReloadFailureCheckpointStore();
        var reactor = new FlakyOnThirdAttemptReactor(checkpointStore);
        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventReader>(eventStore);
        _ = services.AddSingleton(reactor);
        _ = services.AddPortia().AddReactor<FlakyOnThirdAttemptReactor>(WorkloadScope.Global, o =>
        {
            o.Name = "flaky-on-third-attempt-reactor";
            o.PollInterval = TimeSpan.FromMilliseconds(20);
            o.Processing = new ProjectionRunOptions { MaxBatchSize = 2 };
        }).AddWorkers();
        using var provider = services.BuildServiceProvider();

        var hostedService = (BackgroundService)Assert.Single(provider.GetServices<IHostedService>());
        await hostedService.StartAsync(default);
        var executeTask = hostedService.ExecuteTask
            ?? throw new InvalidOperationException("The reactor hosted service did not start.");
        var firstCompletion = await Task.WhenAny(checkpointStore.CheckpointReloaded, executeTask);
        Assert.Same(checkpointStore.CheckpointReloaded, firstCompletion);
        await checkpointStore.CheckpointReloaded;
        await WaitUntilAsync(() => reactor.HandledValues.Count >= 4);
        await hostedService.StopAsync(default);

        // Batch 1 (events 1-2) was saved durably before the fault on event 3. If the workload
        // loop had retried from a stale, un-reloaded local checkpoint instead of the store's,
        // events 1-2 would appear a second time here.
        Assert.Equal([1, 2, 3, 4], reactor.HandledValues);
        Assert.False(executeTask.IsFaulted);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition());
    }

    static T Committed<T>(T ev, Uuid aggregateId, ulong aggregateVersion)
        where T : DomainEvent
    {
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregateId, aggregateVersion, DateTimeOffset.UtcNow));
        return ev;
    }
}

sealed class TransientReloadFailureCheckpointStore : IProjectionCheckpointStore
{
    readonly InMemoryProjectionCheckpointStore _inner = new();
    readonly TaskCompletionSource _checkpointReloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _loadAttempts;

    public Task CheckpointReloaded => _checkpointReloaded.Task;

    public async ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
    {
        var loadAttempt = Interlocked.Increment(ref _loadAttempts);

        if (loadAttempt == 2)
            throw new InvalidOperationException("Simulated transient checkpoint reload failure.");

        var checkpoint = await _inner.LoadAsync(identity, ct);

        if (loadAttempt == 3)
            _ = _checkpointReloaded.TrySetResult();

        return checkpoint;
    }

    public ValueTask SaveAsync(
        CheckpointIdentity identity,
        ProjectionCheckpoint checkpoint,
        CancellationToken ct = default) => _inner.SaveAsync(identity, checkpoint, ct);
}

sealed partial class FlakyOnThirdAttemptReactor(IProjectionCheckpointStore? checkpoints = null)
    : BaseBatchReactor(checkpoints ?? new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("test", "reactors"), "flaky-on-third-attempt-reactor"), IReactorHandler<ValueChanged>
{
    bool _hasFailedOnce;


    public List<int> HandledValues { get; } = [];

    public ValueTask HandleAsync(IReactorContext<ValueChanged> context, CancellationToken ct)
    {
        if (context.Ev.Value == 3 && !_hasFailedOnce)
        {
            _hasFailedOnce = true;
            throw new InvalidOperationException("Simulated transient failure reacting to the third event.");
        }

        HandledValues.Add(context.Ev.Value);
        return ValueTask.CompletedTask;
    }
}

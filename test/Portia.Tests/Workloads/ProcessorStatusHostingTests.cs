using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Status endpoint availability must not gate healthy event processing.</summary>
public sealed class ProcessorStatusHostingTests
{
    /// <summary>Projectors and reactors keep processing while an initially unavailable status endpoint retries.</summary>
    [Fact]
    public async Task ShouldProcessWhileStatusRegistrationFailsAndEventuallyRegisterBothEndpoints()
    {
        // Arrange
        var source = new InMemoryEventStore();
        var aggregate = Uuid.CreateVersion4();
        var changed = new ValueChanged(1);
        changed.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregate, 1, DateTimeOffset.UtcNow));
        await source.AppendAsync(new EventStreamAddress("status-hosting", "source", aggregate.ToString()), 0, [changed]);
        var control = new StatusHostingControl();
        var endpoints = new FailingStatusRegistrar();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEventStore>(source);
        _ = services.AddSingleton<IDomainEventReader>(source);
        _ = services.AddSingleton(control);
        _ = services.AddSingleton<StatusHostingStore>();
        _ = services.AddSingleton<IProcessorStatusRpcRegistrar>(endpoints);
        _ = services.AddPortia().UseSingleProcessWorkloads()
            .AddProjector<StatusHostingProjector>("projector", WorkloadScope.Global,
                options => options.PollInterval = TimeSpan.FromMilliseconds(10))
            .AddReactor<StatusHostingReactor>("reactor", WorkloadScope.Global,
                options => options.PollInterval = TimeSpan.FromMilliseconds(10))
            .AddWorkers();
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().OfType<PortiaWorkloadService>().Single();

        // Act
        await worker.StartAsync(default);

        // Assert
        try
        {
            await Task.WhenAll(control.Projected.Task, control.Reacted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, endpoints.Active);
            endpoints.AllowRegistration.TrySetResult();
            await Task.WhenAll(endpoints.ProjectorRegistered.Task, endpoints.ReactorRegistered.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(worker.ExecuteTask!.IsCompleted);
            Assert.Equal(2, endpoints.Active);
            Assert.Equal(2, endpoints.Attempts["projector"]);
            Assert.Equal(2, endpoints.Attempts["reactor"]);
        }
        finally
        {
            await worker.StopAsync(default);
            worker.Dispose();
        }
        Assert.Equal(0, endpoints.Active);
    }

    sealed class StatusHostingControl
    {
        internal TaskCompletionSource Projected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Reacted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class StatusHostingProjector(StatusHostingStore store, StatusHostingControl control)
        : Projector(store, EventStreamPattern.ForPattern("status-hosting", "source"))
    {
        protected override ValueTask<Result> ProjectEventAsync(DomainEventRecord record,
            IProjectorContext context, CancellationToken ct)
        {
            control.Projected.TrySetResult();
            return ValueTask.FromResult(Result.Success);
        }
    }

    sealed class StatusHostingReactor(StatusHostingStore store, StatusHostingControl control)
        : Reactor(store, EventStreamPattern.ForPattern("status-hosting", "source"))
    {
        protected override ValueTask<Result> ReactToEventAsync(DomainEventRecord record,
            IExecutionContext context, CancellationToken ct)
        {
            control.Reacted.TrySetResult();
            return ValueTask.FromResult(Result.Success);
        }
    }

    sealed class StatusHostingStore : IProjectionStore, IProjectionCheckpointStore
    {
        readonly ConcurrentDictionary<CheckpointIdentity, ProjectionCheckpoint> _checkpoints = new();

        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default)
            => ValueTask.FromResult(_checkpoints.GetValueOrDefault(identity));

        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
            => LoadCheckpointAsync(identity, ct);

        public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            _checkpoints[identity] = checkpoint;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
            => ValueTask.FromResult<IProjectionBatch>(new StatusHostingBatch(this, context.Identity));

        sealed class StatusHostingBatch(StatusHostingStore store, CheckpointIdentity identity) : IProjectionBatch
        {
            public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
                => store.SaveAsync(identity, checkpoint, ct);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    sealed class FailingStatusRegistrar : IProcessorStatusRpcRegistrar
    {
        internal ConcurrentDictionary<string, int> Attempts { get; } = new();
        internal TaskCompletionSource ProjectorRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReactorRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowRegistration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _active;
        internal int Active => Volatile.Read(ref _active);

        public async ValueTask<IAsyncDisposable> RegisterAsync(WorkloadIdentity workload, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Attempts.AddOrUpdate(workload.Name, 1, static (_, count) => count + 1) == 1)
                throw new IOException("Status RPC unavailable.");
            await AllowRegistration.Task.WaitAsync(ct);
            _ = Interlocked.Increment(ref _active);
            (workload.Name == "projector" ? ProjectorRegistered : ReactorRegistered).TrySetResult();
            return new StatusEndpoint(this);
        }

        sealed class StatusEndpoint(FailingStatusRegistrar owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                _ = Interlocked.Decrement(ref owner._active);
                return ValueTask.CompletedTask;
            }
        }
    }
}

using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>The hosted lifetime includes both participants even when cancellation callbacks fail.</summary>
public sealed class WorkloadSupervisionTests
{
    /// <summary>Cancellation faults cannot abandon asynchronous tenant cleanup.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldObserveTenantCleanupGivenCancellationCallbackThrows(bool throwsOnCancel)
    {
        // Arrange
        var directory = new ThrowingCancellationDirectory(throwsOnCancel);
        await using var provider = WorkloadLifecycleTestHost.Create(directory);
        var coordinator = new FaultAfterDirectoryStarts(directory);
        var descriptor = new WorkloadLifecycleDescriptor(_ => ValueTask.FromResult(default(ProjectionPassResult)), () => EventStreamPattern.ForTenant("events"));
        using var worker = WorkloadLifecycleTestHost.Worker(provider, descriptor, coordinator: coordinator, directory: directory);
        await worker.StartAsync(default);

        // Act
        await directory.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _ = await Record.ExceptionAsync(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromMilliseconds(100)));
        var finishedWhileCleanupPending = worker.ExecuteTask!.IsCompleted && !directory.Disposed;
        directory.ReleaseCleanup.TrySetResult();
        await directory.CleanupDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var failure = await Record.ExceptionAsync(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));
        await worker.StopAsync(default);

        // Assert
        Assert.Same(coordinator.Failure, failure);
        if (throwsOnCancel)
            Assert.IsType<AggregateException>(failure.Data["Portia.CleanupException"]);
        Assert.False(finishedWhileCleanupPending, "The hosted workload service finished before observing its tenant manager cleanup.");
    }

    sealed class FaultAfterDirectoryStarts(ThrowingCancellationDirectory directory) : IWorkloadCoordinator
    {
        internal IOException Failure { get; } = new("coordinator failed");
        public async Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads, Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default)
        {
            await directory.Started.Task;
            throw Failure;
        }
    }

    sealed class ThrowingCancellationDirectory(bool throwsOnCancel) : IResumableTenantDirectory
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        internal bool ThrowsOnCancel => throwsOnCancel;
        public ValueTask<ITenantDirectoryCursor> OpenCursorAsync(CancellationToken ct = default) => ValueTask.FromResult<ITenantDirectoryCursor>(new Cursor(this));
        public IAsyncEnumerable<TenantId> GetActiveTenantsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TenantLifecycleChange> WatchAsync(CancellationToken ct = default) => throw new NotSupportedException();
        sealed class Cursor(ThrowingCancellationDirectory owner) : ITenantDirectoryCursor
        {
            CancellationTokenRegistration _registration;
            public async IAsyncEnumerable<TenantLifecycleChange> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                _registration = ct.Register(() => { if (owner.ThrowsOnCancel) throw new IOException("directory cancellation callback failed"); });
                owner.Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                yield break;
            }
            public async ValueTask DisposeAsync()
            {
                owner.DisposalStarted.TrySetResult();
                await owner.ReleaseCleanup.Task;
                _registration.Dispose();
                owner.Disposed = true;
                owner.CleanupDone.TrySetResult();
            }
        }
    }
}

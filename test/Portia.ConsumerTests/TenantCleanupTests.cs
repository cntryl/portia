using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Cntryl.Portia.Consumer;

public sealed class TenantCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFaultsDoNotAbandonOtherTenantsOrDirectory(bool removeFirst)
    {
        var directory = new Directory();
        using var cancellation = new CancellationTokenSource();
        var started = new ConcurrentDictionary<TenantId, CancellationToken>();
        var stopped = new ConcurrentDictionary<TenantId, int>();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new MultiTenantRunner(directory);
        var run = runner.RunAsync(async (tenant, ct) =>
        {
            using var registration = ct.Register(() => throw new IOException("Cancellation callback failed"));
            started[tenant] = ct;
            if (started.Count == 3)
                _ = allStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, (tenant, stopToken) =>
        {
            _ = stopped.AddOrUpdate(tenant, 1, (_, count) => count + 1);
            throw new IOException("Stop callback failed");
        }, cancellation.Token);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (removeFirst)
        {
            await directory.Changes.Writer.WriteAsync(new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, new TenantId("a")));
            await directory.RemovalObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, stopped.GetValueOrDefault(new TenantId("a")));
        }
        // Cancellation callback exceptions must be contained by the runner, including parent cancellation.
        var cancellationError = Record.Exception(cancellation.Cancel);
        var runError = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(cancellationError);
        Assert.Null(runError);
        Assert.Equal(3, stopped.Count);
        Assert.All(stopped.Values, count => Assert.Equal(1, count));
        Assert.All(started.Values, ct => Assert.True(ct.IsCancellationRequested));
    }

    sealed class Directory : ITenantDirectory
    {
        public Channel<TenantLifecycleChange> Changes { get; } = Channel.CreateUnbounded<TenantLifecycleChange>();
        public TaskCompletionSource RemovalObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var name in new[] { "a", "b", "c" })
                yield return new TenantId(name);
        }
        public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var change in Changes.Reader.ReadAllAsync(ct))
            {
                yield return change;
                _ = RemovalObserved.TrySetResult();
            }
        }
    }
}

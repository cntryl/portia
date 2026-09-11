namespace Cntryl.Portia;

/// <summary>
///     Every bounded wait inside a runner is scheduled through the runner's injected
///     <see cref="TimeProvider" />, so a host that supplies one controls all of its timing rather than
///     most of it.
/// </summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class RunnerClockTests
{
    /// <summary>
    ///     Verifies <see cref="MultiTenantRunner" />'s shutdown grace is measured by the injected clock,
    ///     not the system timer, so a tenant callback that ignores cancellation is released when that
    ///     clock passes the grace interval.
    /// </summary>
    [Fact]
    public async Task ShouldBoundMultiTenantShutdownGraceWithTheInjectedClock()
    {
        var grace = TimeSpan.FromMinutes(10);
        var clock = new ManualTestClock();
        var runner = new MultiTenantRunner(new SingleTenantDirectory(),
            new MultiTenantRunnerOptions { ShutdownGrace = grace }, null, clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();
        var run = runner.RunAsync((_, _) =>
        {
            _ = started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        }, (_, _) => Task.CompletedTask, lifetime.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.Cancel();
        Assert.Equal(grace, await clock.WaitForDelayAsync());
        clock.Advance(grace);

        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    ///     Verifies <see cref="FleetPartitionRunner" />'s partition-stop bound is measured by the
    ///     injected clock, so the terminal termination fault is reachable without waiting out the
    ///     configured interval in real time.
    /// </summary>
    [Fact]
    public async Task ShouldBoundPartitionTerminationWithTheInjectedClock()
    {
        const string partition = "lease://portia/fleet/stuck";
        var timeout = TimeSpan.FromMinutes(10);
        var clock = new ManualTestClock();
        var runner = new FleetPartitionRunner(new InMemoryLeaseClient(), new SingleWorkerMembership(), null, clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();
        var options = SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)) with
        {
            PartitionStopTimeout = timeout
        };
        var run = runner.RunAsync([partition], (_, _) =>
        {
            _ = started.TrySetResult();
            return release.Task;
        }, options, lifetime.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.Cancel();
        await WaitForScheduledAsync(clock, timeout);
        clock.Advance(timeout);
        var fault = await Assert.ThrowsAsync<FleetPartitionTerminationTimeoutException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal([partition], fault.Partitions);
        release.SetResult();
    }

    // The reconciliation loop schedules its own delays on the same clock, so wait for the one the
    // termination bound asks for rather than assuming it is the next.
    static async Task WaitForScheduledAsync(ManualTestClock clock, TimeSpan expected)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await clock.WaitForDelayAsync() == expected)
                return;
        }

        Assert.Fail($"No delay of {expected} was scheduled on the injected clock.");
    }

    sealed class SingleTenantDirectory : ITenantDirectory
    {
        public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield return new TenantId("acme");
        }

        public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }
    }
}

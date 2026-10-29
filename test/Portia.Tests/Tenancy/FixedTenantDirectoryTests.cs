using System.Collections.Concurrent;

namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="FixedTenantDirectory" /> honors the directory contract a single-tenant or
///     configured-tenant deployment needs: a stable membership, and a watch that stays open rather than
///     completing, which the runner would otherwise report as a fault and reconnect to every second.
/// </summary>
public sealed class FixedTenantDirectoryTests
{
    /// <summary>The directory reports its tenants in the given order, and an empty directory is allowed.</summary>
    [Fact]
    public async Task ShouldReportConfiguredTenants()
    {
        var directory = new FixedTenantDirectory(new TenantId("acme"), new TenantId("beta"));

        Assert.Equal([new TenantId("acme"), new TenantId("beta")], await directory.GetActiveTenantsAsync().ToListAsync());
        Assert.Empty(await new FixedTenantDirectory().GetActiveTenantsAsync().ToListAsync());
    }

    /// <summary>A tenant listed twice is a configuration mistake, not two workloads.</summary>
    [Fact]
    public void ShouldRejectDuplicateTenants() =>
        Assert.Throws<ArgumentException>(() => new FixedTenantDirectory(new TenantId("acme"), new TenantId("acme")));

    /// <summary>The watch stays open until cancelled and ends through cancellation, never on its own.</summary>
    [Fact]
    public async Task ShouldKeepWatchOpenUntilCancelled()
    {
        var directory = new FixedTenantDirectory(new TenantId("acme"));
        using var cancellation = new CancellationTokenSource();
        var watch = directory.WatchAsync(cancellation.Token).ToListAsync().AsTask();

        Assert.NotSame(watch, await Task.WhenAny(watch, Task.Delay(100)));

        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await watch);
    }

    /// <summary>The runner starts a workload for each tenant and stops them all on shutdown.</summary>
    [Fact]
    public async Task ShouldStartEveryTenantUnderTheRunner()
    {
        var directory = new FixedTenantDirectory(new TenantId("acme"), new TenantId("beta"));
        var runner = new MultiTenantRunner(directory);
        var started = new ConcurrentDictionary<TenantId, TaskCompletionSource>();
        using var shutdown = new CancellationTokenSource();
        var run = runner.RunAsync(async (tenant, ct) =>
        {
            started.GetOrAdd(tenant, _ => new TaskCompletionSource()).TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, (_, _) => Task.CompletedTask, shutdown.Token);

        await Task.WhenAll(
            started.GetOrAdd(new TenantId("acme"), _ => new TaskCompletionSource()).Task,
            started.GetOrAdd(new TenantId("beta"), _ => new TaskCompletionSource()).Task).WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, started.Count);
    }
}

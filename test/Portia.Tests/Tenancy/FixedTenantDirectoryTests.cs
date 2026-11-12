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
        // Arrange
        var directory = new FixedTenantDirectory(new TenantId("acme"), new TenantId("beta"));
        var empty = new FixedTenantDirectory();

        // Act
        var tenants = await directory.GetActiveTenantsAsync().ToListAsync();
        var emptyTenants = await empty.GetActiveTenantsAsync().ToListAsync();

        // Assert
        Assert.Equal([new TenantId("acme"), new TenantId("beta")], tenants);
        Assert.Empty(emptyTenants);
    }

    /// <summary>A tenant listed twice is a configuration mistake, not two workloads.</summary>
    [Fact]
    public void ShouldRejectDuplicateTenants()
    {
        // Arrange
        var tenant = new TenantId("acme");

        // Act
        var exception = Record.Exception(() => new FixedTenantDirectory(tenant, tenant));

        // Assert
        _ = Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>The watch stays open until cancelled and ends through cancellation, never on its own.</summary>
    [Fact]
    public async Task ShouldKeepWatchOpenUntilCancelled()
    {
        // Arrange
        var directory = new FixedTenantDirectory(new TenantId("acme"));
        using var cancellation = new CancellationTokenSource();

        // Act
        var watch = directory.WatchAsync(cancellation.Token).ToListAsync().AsTask();

        // Assert
        await Assert.ThrowsAsync<TimeoutException>(() => watch.WaitAsync(TimeSpan.FromMilliseconds(100)));

        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await watch);
    }

    /// <summary>The runner starts a workload for each tenant and stops them all on shutdown.</summary>
    [Fact]
    public async Task ShouldStartEveryTenantUnderTheRunner()
    {
        // Arrange
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

        // Act
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(2, started.Count);
    }
}

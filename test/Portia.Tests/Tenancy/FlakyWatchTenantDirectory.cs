using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>
///     An <see cref="ITenantDirectory" /> whose first <see cref="WatchAsync" /> subscription throws
///     as soon as it's iterated, and whose second attempt yields one tenant and then blocks forever
///     (matching a real, healthy watch stream) — for proving a runner reconnects after a transient
///     failure instead of dying with it.
/// </summary>
sealed class FlakyWatchTenantDirectory : ITenantDirectory
{
    int _watchAttempts;

    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield break;
    }

    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var attempt = Interlocked.Increment(ref _watchAttempts);
        await Task.Yield();
        _ = attempt == 1 ? throw new InvalidOperationException("Simulated transient watch stream failure.") : attempt;

        yield return new TenantLifecycleChange(TenantLifecycleChangeKind.Added,
            new TenantId("recovered-after-reconnect"));

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (ct.Register(() => tcs.TrySetResult()))
            await tcs.Task.ConfigureAwait(false);
    }
}

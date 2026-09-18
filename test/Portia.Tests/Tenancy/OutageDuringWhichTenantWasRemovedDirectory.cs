using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>
///     Models a stateful directory (like <see cref="EventSourcedTenantDirectory{TStart,TStop}" />)
///     across an outage: the first <c>GetActiveTenantsAsync</c> call reports "acme" active, the
///     first <c>WatchAsync</c> subscription then throws, and the *second* <c>GetActiveTenantsAsync</c>
///     call (the reconnect snapshot) reports nothing at all — as if "acme" had been deregistered
///     during the outage and that removal folded silently into the directory's own internal offset,
///     never surfaced as its own change.
/// </summary>
sealed class OutageDuringWhichTenantWasRemovedDirectory : ITenantDirectory
{
    int _snapshotAttempts;
    int _watchAttempts;

    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var attempt = Interlocked.Increment(ref _snapshotAttempts);
        await Task.Yield();

        if (attempt == 1)
        {
            yield return new TenantId("acme");
        }

        // Second and later snapshots report nothing — "acme" is gone, but never as a change.
    }

    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var attempt = Interlocked.Increment(ref _watchAttempts);
        await Task.Yield();

        if (attempt == 1)
        {
            throw new InvalidOperationException("Simulated transient watch stream failure.");
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (ct.Register(() => tcs.TrySetResult()))
            await tcs.Task.ConfigureAwait(false);

        yield break;
    }
}

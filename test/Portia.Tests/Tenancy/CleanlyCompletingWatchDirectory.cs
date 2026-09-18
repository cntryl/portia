using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>
///     A directory whose <c>WatchAsync</c> subscription always ends cleanly and immediately — never
///     throwing, never blocking — for proving a runner still backs off between reconnect attempts
///     instead of spinning as fast as this allows.
/// </summary>
sealed class CleanlyCompletingWatchDirectory : ITenantDirectory
{
    public int WatchAttempts { get; private set; }

    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield break;
    }

    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        WatchAttempts++;
        await Task.Yield();
        yield break;
    }
}

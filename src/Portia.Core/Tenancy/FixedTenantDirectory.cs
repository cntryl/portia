using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>
///     A tenant directory whose membership is fixed when the host starts: the tenants given here are
///     active for the life of the process, and none is ever added or removed. It is the directory for a
///     single-tenant deployment, a configured short list of tenants, and tests. A deployment whose tenants
///     change at runtime uses <see cref="EventSourcedTenantDirectory{TStartEvent, TStopEvent}" /> instead.
/// </summary>
public sealed class FixedTenantDirectory : ITenantDirectory
{
    readonly TenantId[] _tenants;

    /// <summary>Creates a directory that serves exactly these tenants.</summary>
    /// <param name="tenants">The active tenants, in the order they are reported.</param>
    /// <exception cref="ArgumentException">A tenant appears more than once.</exception>
    public FixedTenantDirectory(params IEnumerable<TenantId> tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        _tenants = [.. tenants];
        if (_tenants.Distinct().Count() != _tenants.Length)
            throw new ArgumentException("Each tenant can appear only once.", nameof(tenants));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var tenant in _tenants)
        {
            ct.ThrowIfCancellationRequested();
            yield return tenant;
        }
    }

    /// <summary>
    ///     Reports no changes, ever: the watch stays open until <paramref name="ct" /> is cancelled, as the
    ///     contract requires, because membership cannot change.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>An empty sequence that completes only through cancellation.</returns>
    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        yield break;
    }
}

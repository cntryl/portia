namespace Cntryl.Portia;

/// <summary>Describes whether a write guarded by a fencing token was applied.</summary>
public enum FencedWriteOutcome
{
    /// <summary>The write was durably applied.</summary>
    Applied,
    /// <summary>The write was rejected without changing the target.</summary>
    Rejected,
}

/// <summary>
/// Adapts an application's durable target to the reusable fencing-token conformance suite.
/// Implementations must retain state across <see cref="ReopenAsync" />.
/// </summary>
public interface IFencingTokenConformanceProbe
{
    /// <summary>Clears the isolated conformance target.</summary>
    ValueTask ResetAsync(CancellationToken ct = default);
    /// <summary>Attempts to store a value under the supplied ownership token.</summary>
    ValueTask<FencedWriteOutcome> WriteAsync(ulong fencingToken, string value, CancellationToken ct = default);
    /// <summary>Reads the value currently committed by the target.</summary>
    ValueTask<string?> ReadAsync(CancellationToken ct = default);
    /// <summary>Closes and reopens the durable implementation without clearing its state.</summary>
    ValueTask ReopenAsync(CancellationToken ct = default);
}

/// <summary>Reusable fencing-token conformance scenarios for application persistence implementations.</summary>
public static class FencingTokenConformance
{
    /// <summary>Verifies monotonic ownership, stale-write rejection, durability, and concurrency.</summary>
    /// <param name="probe">An isolated durable target adapter.</param>
    /// <param name="ct">A token that can cancel verification.</param>
    /// <exception cref="ConformanceViolationException">The target violates a fencing invariant.</exception>
    public static async ValueTask VerifyAsync(IFencingTokenConformanceProbe probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        await probe.ResetAsync(ct).ConfigureAwait(false);
        await RequireOutcome(probe, 0, "zero", FencedWriteOutcome.Rejected, ct).ConfigureAwait(false);
        await RequireOutcome(probe, 10, "owner-10", FencedWriteOutcome.Applied, ct).ConfigureAwait(false);
        await RequireOutcome(probe, 10, "owner-10-repeat", FencedWriteOutcome.Applied, ct).ConfigureAwait(false);
        await RequireValue(probe, "owner-10-repeat", "current fencing token write", ct).ConfigureAwait(false);
        await RequireOutcome(probe, 11, "owner-11", FencedWriteOutcome.Applied, ct).ConfigureAwait(false);
        await RequireOutcome(probe, 10, "stale-owner-10", FencedWriteOutcome.Rejected, ct).ConfigureAwait(false);
        await RequireValue(probe, "owner-11", "stale fencing token write", ct).ConfigureAwait(false);

        await probe.ReopenAsync(ct).ConfigureAwait(false);
        await RequireOutcome(probe, 10, "stale-after-reopen", FencedWriteOutcome.Rejected, ct).ConfigureAwait(false);
        await RequireValue(probe, "owner-11", "stale write after reopen", ct).ConfigureAwait(false);

        var lower = probe.WriteAsync(20, "concurrent-20", ct).AsTask();
        var higher = probe.WriteAsync(21, "concurrent-21", ct).AsTask();
        _ = await Task.WhenAll(lower, higher).ConfigureAwait(false);
        await RequireValue(probe, "concurrent-21", "concurrent fencing writes", ct).ConfigureAwait(false);
        await RequireOutcome(probe, 20, "stale-after-concurrency", FencedWriteOutcome.Rejected, ct).ConfigureAwait(false);
    }

    static async ValueTask RequireOutcome(IFencingTokenConformanceProbe probe, ulong token, string value,
        FencedWriteOutcome expected, CancellationToken ct)
    {
        var actual = await probe.WriteAsync(token, value, ct).ConfigureAwait(false);
        if (actual != expected)
        {
            throw new ConformanceViolationException(
                $"Fencing write '{value}' with token {token} returned {actual}; expected {expected}.");
        }
    }

    static async ValueTask RequireValue(IFencingTokenConformanceProbe probe, string expected, string scenario,
        CancellationToken ct)
    {
        var actual = await probe.ReadAsync(ct).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new ConformanceViolationException(
                $"The {scenario} changed the committed value to '{actual ?? "<null>"}'; expected '{expected}'.");
        }
    }
}

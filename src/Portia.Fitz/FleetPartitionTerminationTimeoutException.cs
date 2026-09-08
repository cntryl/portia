namespace Cntryl.Portia;

/// <summary>
/// Indicates that one or more revoked fleet partition callbacks ignored cancellation beyond the
/// configured bound. The runner stops rather than risking replacement work beside stale work.
/// </summary>
/// <param name="partitions">The exact partition routes whose callbacks did not terminate.</param>
/// <param name="timeout">The configured shared termination bound.</param>
public sealed class FleetPartitionTerminationTimeoutException(
    IReadOnlyCollection<string> partitions, TimeSpan timeout)
    : TimeoutException($"Fleet partition work did not terminate within {timeout}.")
{
    /// <summary>Gets the affected exact partition routes in ordinal order.</summary>
    public IReadOnlyList<string> Partitions { get; } = partitions?.Order(StringComparer.Ordinal).ToArray()
        ?? throw new ArgumentNullException(nameof(partitions));

    /// <summary>Gets the configured shared termination bound.</summary>
    public TimeSpan Timeout { get; } = timeout > TimeSpan.Zero
        ? timeout
        : throw new ArgumentOutOfRangeException(nameof(timeout));
}

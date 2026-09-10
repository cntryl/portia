namespace Cntryl.Portia;

/// <summary>
///     Configures one projector run without changing projector behavior.
/// </summary>
public sealed record ProjectionRunOptions
{
    /// <summary>
    ///     Gets the default projection run options.
    /// </summary>
    public static ProjectionRunOptions Default { get; } = new();

    /// <summary>
    ///     Gets the maximum number of events committed in one batch.
    /// </summary>
    public int MaxBatchSize { get; init; } = 512;

    /// <summary>
    ///     Gets the rebuild generation, or null for live processing. Reuse an ID to resume its data and progress.
    /// </summary>
    public string? RebuildId { get; init; }

    /// <summary>Rejects invalid batching and generation settings before work starts.</summary>
    public void Validate()
    {
        if (RebuildId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(RebuildId);
        }

        if (MaxBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBatchSize), "Batch size must be greater than zero.");
        }
    }
}

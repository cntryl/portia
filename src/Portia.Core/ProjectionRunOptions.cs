namespace Cntryl.Portia;

/// <summary>
/// Configures one projector run without changing projector behavior.
/// </summary>
public sealed record ProjectionRunOptions
{
    /// <summary>
    /// Gets the default projection run options.
    /// </summary>
    public static ProjectionRunOptions Default { get; } = new();

    /// <summary>
    /// Gets the maximum number of events committed in one batch.
    /// </summary>
    public int MaxBatchSize { get; init; } = 512;

    /// <summary>
    /// Gets whether the run writes to a rebuild generation.
    /// </summary>
    public bool IsRebuild { get; init; }

    internal void Validate()
    {
        if (MaxBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBatchSize), "Batch size must be greater than zero.");
    }
}

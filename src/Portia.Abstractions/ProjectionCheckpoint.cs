namespace Cntryl.Portia;

/// <summary>
///     Identifies the next scope offset a projector must read.
/// </summary>
/// <param name="NextOffset">The next inclusive realm or area offset.</param>
public readonly record struct ProjectionCheckpoint(ulong NextOffset)
{
    /// <summary>
    ///     Gets the checkpoint for a projector that has not processed any events.
    /// </summary>
    public static ProjectionCheckpoint Start => default;
}

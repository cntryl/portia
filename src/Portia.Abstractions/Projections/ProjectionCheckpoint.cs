namespace Cntryl.Portia;

/// <summary>
///     Identifies the opaque position from which a projector or reactor resumes.
/// </summary>
/// <param name="Cursor">The event reader cursor.</param>
public readonly record struct ProjectionCheckpoint(EventCursor Cursor)
{
    /// <summary>
    ///     Gets the checkpoint for a projector that has not processed any events.
    /// </summary>
    public static ProjectionCheckpoint Start => default;
}

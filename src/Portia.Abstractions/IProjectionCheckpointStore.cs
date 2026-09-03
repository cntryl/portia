namespace Cntryl.Portia;

/// <summary>
/// Persists a projector's or reactor's checkpoint between passes — the piece a hosted polling
/// loop needs that <see cref="ProjectionCheckpoint" /> alone can't supply, since where a
/// checkpoint lives between runs is inherently app-specific (a database row, a KV entry, a file).
/// </summary>
public interface IProjectionCheckpointStore
{
    /// <summary>
    /// Loads the checkpoint to resume from, or the zero checkpoint if none has been saved yet.
    /// </summary>
    /// <param name="name">The stable projector or reactor name (<c>Projector{TProjection}.Name</c>
    /// / <c>Reactor.Name</c>) the checkpoint belongs to.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The checkpoint to resume from.</returns>
    ValueTask<ProjectionCheckpoint> LoadAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Saves the checkpoint reached after a pass.
    /// </summary>
    /// <param name="name">The stable projector or reactor name the checkpoint belongs to.</param>
    /// <param name="checkpoint">The checkpoint to save.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the save.</returns>
    ValueTask SaveAsync(string name, ProjectionCheckpoint checkpoint, CancellationToken ct = default);
}

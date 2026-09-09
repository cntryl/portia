namespace Cntryl.Portia;

/// <summary>Owns an atomic projection unit of work. Disposal releases resources and discards uncommitted
/// work; it must never commit. Implementations enforce checkpoint concurrency.</summary>
public interface IProjectionBatch : IAsyncDisposable
{
    /// <summary>Atomically commits repository changes and the next checkpoint. A failed response can be
    /// ambiguous: the next attempt must reload authoritative progress before applying events again.</summary>
    ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default);
}

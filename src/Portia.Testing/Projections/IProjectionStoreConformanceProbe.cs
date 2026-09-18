namespace Cntryl.Portia.Testing;

/// <summary>Creates an isolated durable projection implementation for its conformance suite.</summary>
public interface IProjectionStoreConformanceProbe
{
    /// <summary>Gets the live projection identity used by the suite.</summary>
    CheckpointIdentity LiveIdentity { get; }

    /// <summary>Gets a rebuild identity for the same logical projection.</summary>
    CheckpointIdentity RebuildIdentity { get; }

    /// <summary>Clears all data owned by the isolated conformance target.</summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the target is empty.</returns>
    ValueTask ResetAsync(CancellationToken ct = default);

    /// <summary>Opens an independent persistence session against the same durable target.</summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A session the caller disposes when finished with it.</returns>
    ValueTask<IProjectionStoreConformanceSession> OpenSessionAsync(CancellationToken ct = default);
}

namespace Cntryl.Portia.Testing;

/// <summary>Creates an isolated durable projection implementation for its conformance suite.</summary>
public interface IProjectionStoreConformanceProbe
{
    /// <summary>Gets the live projection identity used by the suite.</summary>
    CheckpointIdentity LiveIdentity { get; }

    /// <summary>Gets a rebuild identity for the same logical projection.</summary>
    CheckpointIdentity RebuildIdentity { get; }

    /// <summary>Clears all data owned by the isolated conformance target.</summary>
    ValueTask ResetAsync(CancellationToken ct = default);

    /// <summary>Opens an independent persistence session against the same durable target.</summary>
    ValueTask<IProjectionStoreConformanceSession> OpenSessionAsync(CancellationToken ct = default);
}

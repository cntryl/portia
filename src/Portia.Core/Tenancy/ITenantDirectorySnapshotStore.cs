namespace Cntryl.Portia;

/// <summary>Persists a tenant roster and its lifecycle cursor as one conditional value.</summary>
public interface ITenantDirectorySnapshotStore
{
    /// <summary>Loads the last complete snapshot for a lifecycle stream pattern.</summary>
    ValueTask<TenantDirectorySnapshot?> LoadAsync(EventStreamPattern pattern, CancellationToken ct = default);

    /// <summary>
    ///     Saves a complete snapshot only if the previously loaded cursor is still current.
    ///     Returns false when another writer has advanced the snapshot.
    /// </summary>
    ValueTask<bool> TrySaveAsync(EventStreamPattern pattern, EventCursor? expectedCursor,
        TenantDirectorySnapshot snapshot, CancellationToken ct = default);
}

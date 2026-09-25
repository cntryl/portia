using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>Stores tenant rosters and their event cursors atomically in Fitz KV.</summary>
public sealed class FitzKvTenantDirectorySnapshotStore(IKvClient client, string route) : ITenantDirectorySnapshotStore
{
    static readonly ReadOnlyMemory<byte> Key = "tenant-directory-snapshot-v1"u8.ToArray();
    readonly IKvClient _client = client ?? throw new ArgumentNullException(nameof(client));
    readonly string _route = FitzKvCheckpoints.Route(route, nameof(route));

    /// <inheritdoc />
    public async ValueTask<TenantDirectorySnapshot?> LoadAsync(EventStreamPattern pattern,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        await using var tx = await _client.BeginAsync(Route(pattern), KvDurability.Sync, KvMode.ReadOnly, ct)
            .ConfigureAwait(false);
        var stored = await tx.GetAsync(Key, ct).ConfigureAwait(false);
        return stored.Found ? Decode(stored.Value!.Value.Span) : null;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TrySaveAsync(EventStreamPattern pattern, EventCursor? expectedCursor,
        TenantDirectorySnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(snapshot);
        IKvTransaction tx;
        try
        {
            tx = await _client.BeginAsync(Route(pattern), KvDurability.Sync, KvMode.ReadWrite, ct)
                .ConfigureAwait(false);
        }
        catch (KvException ex) when (ex.DomainCode == FitzErrorCodes.KvIsolationConflict)
        {
            // Another directory is saving this pattern's roster. Its transaction holds the
            // resource lock, so this pass can keep its own cursor and retry on a later pass.
            return false;
        }

        await using (tx)
        {
            var committed = false;
            try
            {
                var stored = await tx.GetAsync(Key, ct).ConfigureAwait(false);
                var actual = stored.Found ? Decode(stored.Value!.Value.Span).Cursor : (EventCursor?)null;
                if (actual != expectedCursor)
                    return false;
                var value = JsonSerializer.SerializeToUtf8Bytes(
                    new FitzTenantDirectorySnapshot(snapshot.Cursor.Value,
                        snapshot.ActiveTenants.Select(tenant => tenant.Value).ToArray()),
                    FitzJsonContext.Default.FitzTenantDirectorySnapshot);
                await tx.PutAsync(Key, value, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                committed = true;
                return true;
            }
            finally
            {
                if (!committed)
                    await FitzKvCheckpoints.RollbackAsync(tx).ConfigureAwait(false);
            }
        }
    }

    string Route(EventStreamPattern pattern) =>
        FitzKvCheckpoints.WorkloadRoute(_route, "tenant-directory", pattern.ToString());

    static TenantDirectorySnapshot Decode(ReadOnlySpan<byte> value)
    {
        var stored = JsonSerializer.Deserialize(value, FitzJsonContext.Default.FitzTenantDirectorySnapshot)
                     ?? throw new InvalidDataException("The persisted tenant directory snapshot is empty.");
        var cursor = stored.Cursor is null ? EventCursor.Start : new EventCursor(stored.Cursor);
        var tenants = stored.Tenants.Select(value => new TenantId(value)).ToArray();
        return new TenantDirectorySnapshot(cursor, tenants);
    }
}

sealed record FitzTenantDirectorySnapshot(string? Cursor, string[] Tenants);

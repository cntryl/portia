using System.Buffers.Binary;
using System.Text;
using Cntryl.Fitz.Abstractions.Domains.Kv;

namespace Cntryl.Portia;

/// <summary>Encodes a <see cref="CheckpointIdentity"/> and <see cref="ProjectionCheckpoint"/> as Fitz KV
/// key/value bytes, shared by <see cref="FitzKvCheckpointStore"/> and <see cref="FitzKvProjectionStore"/>
/// so both encode identically and can be pointed at the same route.</summary>
static class FitzKvCheckpoints
{
    /// <summary>Builds the deterministic key for one checkpoint identity.</summary>
    public static ReadOnlyMemory<byte> Key(CheckpointIdentity identity) =>
        Encoding.UTF8.GetBytes(string.Join(
            '\0', "checkpoint", identity.ComponentName, identity.Pattern, identity.RebuildId ?? string.Empty));

    /// <summary>Encodes a checkpoint's next offset as an 8-byte big-endian value.</summary>
    public static ReadOnlyMemory<byte> Encode(ulong nextOffset)
    {
        var buffer = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, nextOffset);
        return buffer;
    }

    /// <summary>Decodes a checkpoint's next offset from its stored bytes.</summary>
    public static ulong Decode(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt64BigEndian(value);

    /// <summary>Reads the stored checkpoint for an identity, or <see cref="ProjectionCheckpoint.Start"/>
    /// when nothing has been saved yet.</summary>
    public static async ValueTask<ProjectionCheckpoint> LoadAsync(
        IKvClient client, string route, CheckpointIdentity identity, CancellationToken ct)
    {
        await using var tx = await client.BeginAsync(route, KvDurability.Sync, KvMode.ReadOnly, ct).ConfigureAwait(false);
        var result = await tx.GetAsync(Key(identity), ct).ConfigureAwait(false);
        return result.Found ? new ProjectionCheckpoint(Decode(result.Value!.Value.Span)) : ProjectionCheckpoint.Start;
    }

    /// <summary>Best-effort rollback that never replaces the failure that caused it.</summary>
    public static async Task RollbackAsync(IKvTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the put or commit failure that caused the rollback.
        }
    }
}

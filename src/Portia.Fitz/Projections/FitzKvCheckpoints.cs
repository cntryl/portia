using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>
///     Encodes a <see cref="CheckpointIdentity" /> and <see cref="ProjectionCheckpoint" /> as Fitz KV
///     key/value bytes, shared by <see cref="FitzKvCheckpointStore" /> and <see cref="FitzKvProjectionStore" />
///     so both encode identically and can be pointed at the same base route.
/// </summary>
static class FitzKvCheckpoints
{
    static ReadOnlySpan<byte> FormatPrefix => "portia-checkpoint-v1\0"u8;

    /// <summary>
    ///     Validates a Fitz KV route up front, so a misshapen one fails at construction rather than on
    ///     every read and write the broker then rejects for the life of the process.
    /// </summary>
    public static string Route(string? route, string parameterName)
    {
        var segments = route?.StartsWith("kv://", StringComparison.Ordinal) == true
            ? route[5..].Split('/')
            : [];
        return segments.Length == 3 && segments.All(FleetRunOptions.IsSegment)
            ? route!
            : throw new ArgumentException(
                "A Fitz KV route must be kv://{realm}/{area}/{resource} with exact nonblank segments.",
                parameterName);
    }

    /// <summary>Builds the deterministic key for one checkpoint identity.</summary>
    public static ReadOnlyMemory<byte> Key(CheckpointIdentity identity) =>
        Encoding.UTF8.GetBytes(string.Join(
            '\0', "checkpoint", identity.ComponentName, identity.Pattern, identity.RebuildId ?? string.Empty));

    /// <summary>Encodes a backend-owned cursor; the prefix alone represents <see cref="EventCursor.Start" />.</summary>
    public static ReadOnlyMemory<byte> Encode(EventCursor cursor)
    {
        var cursorBytes = Encoding.UTF8.GetBytes(cursor.ToString());
        var value = new byte[FormatPrefix.Length + cursorBytes.Length];
        FormatPrefix.CopyTo(value);
        cursorBytes.CopyTo(value.AsSpan(FormatPrefix.Length));
        return value;
    }

    /// <summary>Decodes a current cursor or a published 0.1.x unsigned big-endian offset.</summary>
    public static EventCursor Decode(ReadOnlySpan<byte> value)
    {
        if (value.StartsWith(FormatPrefix))
            return value.Length == FormatPrefix.Length
                ? EventCursor.Start
                : new EventCursor(Encoding.UTF8.GetString(value[FormatPrefix.Length..]));

        if (value.Length == sizeof(ulong))
        {
            var offset = BinaryPrimitives.ReadUInt64BigEndian(value);
            return new EventCursor(offset.ToString(CultureInfo.InvariantCulture));
        }

        throw new InvalidDataException("The persisted Fitz checkpoint has an unknown encoding.");
    }

    /// <summary>
    ///     Reads the stored checkpoint for an identity, or <see cref="ProjectionCheckpoint.Start" />
    ///     when nothing has been saved yet.
    /// </summary>
    public static async ValueTask<ProjectionCheckpoint> LoadAsync(
        IKvClient client, string route, CheckpointIdentity identity, CancellationToken ct)
    {
        await using var tx = await BeginAsync(client, route, KvMode.ReadOnly, "Checkpoint", identity, ct)
            .ConfigureAwait(false);
        var result = await tx.GetAsync(Key(identity), ct).ConfigureAwait(false);
        return result.Found ? new ProjectionCheckpoint(Decode(result.Value!.Value.Span)) : ProjectionCheckpoint.Start;
    }

    /// <summary>
    ///     Opens a Fitz KV transaction, translating a concurrent writer into the shared
    ///     <see cref="ProjectionConcurrencyException" />. Fitz KV locks a resource at BEGIN rather than
    ///     detecting the conflict at COMMIT, so a losing writer is rejected before it ever stages
    ///     anything — that has to reach the caller as the same retryable failure a commit conflict does,
    ///     or a hosted component sees an untranslated broker exception and never reloads its checkpoint.
    /// </summary>
    public static async Task<IKvTransaction> BeginAsync(IKvClient client, string route, KvMode mode,
        string subject, CheckpointIdentity identity, CancellationToken ct)
    {
        try
        {
            return await client.BeginAsync(route, KvDurability.Sync, mode, ct).ConfigureAwait(false);
        }
        catch (KvException ex) when (ex.DomainCode == FitzErrorCodes.KvIsolationConflict)
        {
            throw Conflict(subject, identity, ex);
        }
    }

    /// <summary>
    ///     Derives the Fitz KV resource one workload — a component in one realm — transacts against
    ///     from a configured base route. Fitz KV locks a resource exclusively for a ReadWrite
    ///     transaction's whole lifetime rather than the keys it touches, so any two workloads sharing a
    ///     resource contend for that one lock purely because they were pointed at one route: different
    ///     reactors behind the app's single checkpoint store, different projectors given the same route,
    ///     or — since every tenant instance of a per-tenant component shares its name and runs
    ///     concurrently — every tenant of one component. Deriving the resource from the component and
    ///     realm makes that sharing impossible to configure. The suffix is a stable hash rather than
    ///     the raw values, since neither is guaranteed to be a legal route segment on its own.
    /// </summary>
    public static string WorkloadRoute(string route, string componentName, string realm)
    {
        var segments = route[5..].Split('/');
        var identity = JsonSerializer.SerializeToUtf8Bytes([componentName, realm],
            FitzJsonContext.Default.StringArray);
        var suffix = Convert.ToHexStringLower(SHA256.HashData(identity))[..16];
        return $"kv://{segments[0]}/{segments[1]}/{segments[2]}-{suffix}";
    }

    /// <summary>
    ///     Derives the resource for the workload a checkpoint belongs to. A bound per-tenant pattern's
    ///     realm is its tenant, so each tenant resolves separately; the rebuild generation is left out
    ///     so a rebuild writes where query-side reads already look, its checkpoint key kept apart.
    /// </summary>
    public static string WorkloadRoute(string route, CheckpointIdentity identity) =>
        WorkloadRoute(route, identity.ComponentName, identity.Pattern.Realm);

    /// <summary>Builds the shared conflict failure, so BEGIN and COMMIT conflicts read alike.</summary>
    public static ProjectionConcurrencyException Conflict(string subject, CheckpointIdentity identity,
        Exception cause) =>
        new($"{subject} for '{identity.ComponentName}' pattern '{identity.Pattern}' conflicted with a "
            + "concurrent writer; reload the authoritative checkpoint before retrying.", cause);

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

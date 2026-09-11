using Cntryl.Fitz.Abstractions;
using Cntryl.Fitz.Abstractions.Domains.Kv;
using Cntryl.Fitz.Errors;

namespace Cntryl.Portia;

/// <summary>
///     Persists reactor checkpoints in Fitz KV — a durable <see cref="IProjectionCheckpointStore" /> for
///     applications that already depend on <c>Portia.Fitz</c> and would rather not stand up a second
///     persistence technology just to survive a restart. Projector checkpoints instead belong to
///     <see cref="FitzKvProjectionStore" />, which commits them atomically with projection data; this type
///     is for reactors, whose checkpoint and effects are never part of one transaction.
/// </summary>
/// <param name="client">The Fitz KV client to open transactions against.</param>
/// <param name="route">The Fitz KV route this component's checkpoints live under.</param>
public sealed class FitzKvCheckpointStore(IKvClient client, string route) : IProjectionCheckpointStore
{
    readonly IKvClient _client = client ?? throw new ArgumentNullException(nameof(client));

    readonly string _route = FitzKvCheckpoints.Route(route, nameof(route));

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return FitzKvCheckpoints.LoadAsync(_client, _route, identity, ct);
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var tx = await FitzKvCheckpoints
            .BeginAsync(_client, _route, KvMode.ReadWrite, "Checkpoint", identity, ct).ConfigureAwait(false);
        var failed = false;
        try
        {
            await tx.PutAsync(FitzKvCheckpoints.Key(identity), FitzKvCheckpoints.Encode(checkpoint.NextOffset), ct)
                .ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            await FitzKvCheckpoints.RollbackAsync(tx).ConfigureAwait(false);

            if (ex is KvException { DomainCode: FitzErrorCodes.KvIsolationConflict })
            {
                throw FitzKvCheckpoints.Conflict("Checkpoint", identity, ex);
            }
            else
            {
                throw;
            }
        }
        finally
        {
            try
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
            catch when (failed)
            {
                // Cleanup must not replace the put or commit failure seen by the caller.
            }
        }
    }
}

using Cntryl.Fitz.Abstractions;
using Cntryl.Fitz.Abstractions.Domains.Kv;
using Cntryl.Fitz.Errors;

namespace Cntryl.Portia;

/// <summary>
///     Base for an application repository that projects into Fitz KV, sharing one
///     <see cref="IKvTransaction" /> between the repository's own domain writes and the checkpoint
///     Portia commits with them — the KV analog of an EF Core repository sharing one
///     <c>DbContext</c> transaction between application writes and checkpoint advancement. A derived
///     repository performs its own <c>GetAsync</c>/<c>PutAsync</c>/<c>DeleteAsync</c>/<c>ScanAsync</c>
///     calls against <see cref="Transaction" /> while a batch is open; Portia commits both the
///     repository's writes and the checkpoint in that same Fitz KV transaction.
/// </summary>
/// <param name="client">The Fitz KV client to open transactions against.</param>
/// <param name="route">The Fitz KV route this repository's data and checkpoint live under.</param>
public abstract class FitzKvProjectionStore(IKvClient client, string route) : IProjectionStore
{
    readonly IKvClient _client = client ?? throw new ArgumentNullException(nameof(client));

    readonly string _route = string.IsNullOrWhiteSpace(route)
        ? throw new ArgumentException("A Fitz KV route cannot be empty.", nameof(route))
        : route;

    /// <summary>
    ///     Gets the transaction the current unit of work writes through. Only valid between
    ///     <see cref="BeginAsync" /> and the returned batch's commit or disposal — a derived
    ///     repository's own domain write methods use this so their writes land in the same atomic
    ///     commit as the checkpoint. Never open a second transaction against the same route while
    ///     this one is in flight; Fitz KV isolation conflicts, it does not merge concurrent writers.
    /// </summary>
    protected IKvTransaction Transaction { get; private set; } = null!;

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return FitzKvCheckpoints.LoadAsync(_client, _route, identity, ct);
    }

    /// <inheritdoc />
    public async ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Transaction = await _client.BeginAsync(_route, KvDurability.Sync, KvMode.ReadWrite, ct).ConfigureAwait(false);
        return new Batch(this, context.Identity);
    }

    sealed class Batch(FitzKvProjectionStore store, CheckpointIdentity identity) : IProjectionBatch
    {
        bool _done;

        public async ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            var tx = store.Transaction;
            try
            {
                await tx.PutAsync(FitzKvCheckpoints.Key(identity), FitzKvCheckpoints.Encode(checkpoint.NextOffset), ct)
                    .ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                _done = true;
            }
            catch (Exception ex)
            {
                _done = true;
                await FitzKvCheckpoints.RollbackAsync(tx).ConfigureAwait(false);

                if (ex is KvException { DomainCode: FitzErrorCodes.KvIsolationConflict })
                {
                    throw new ProjectionConcurrencyException(
                        $"Projection batch for '{identity.ComponentName}' pattern '{identity.Pattern}' conflicted with a concurrent writer; reload the authoritative checkpoint before retrying.",
                        ex);
                }
                else
                {
                    throw;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            var tx = store.Transaction;
            if (!_done)
                await FitzKvCheckpoints.RollbackAsync(tx).ConfigureAwait(false);

            await tx.DisposeAsync().ConfigureAwait(false);
        }
    }
}

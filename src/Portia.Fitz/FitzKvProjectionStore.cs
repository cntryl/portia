
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

    readonly string _route = FitzKvCheckpoints.Route(route, nameof(route));

    IKvTransaction? _open;

    /// <summary>
    ///     Gets the transaction the current unit of work writes through. Only valid between
    ///     <see cref="BeginAsync" /> and the returned batch's commit or disposal — a derived
    ///     repository's own domain write methods use this so their writes land in the same atomic
    ///     commit as the checkpoint. Never open a second transaction against the same route while
    ///     this one is in flight; Fitz KV isolation conflicts, it does not merge concurrent writers.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     No projection batch is open. Reading this outside a batch would otherwise hand back the
    ///     previous batch's disposed transaction, where a repository write is staged into a unit of
    ///     work that has already ended and is silently lost.
    /// </exception>
    protected IKvTransaction Transaction => _open ?? throw new InvalidOperationException(
        "No projection batch is open on this store. A repository write must happen between "
        + "BeginAsync and the returned batch's commit or disposal, so it shares the checkpoint's transaction.");

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
        if (_open is not null)
        {
            throw new InvalidOperationException(
                "A projection batch is already open on this store; commit or dispose it before beginning another. "
                + "One store instance owns one Fitz KV transaction, so a second batch would retarget the first one's writes.");
        }

        _open = await FitzKvCheckpoints
            .BeginAsync(_client, _route, KvMode.ReadWrite, "Projection batch", context.Identity, ct)
            .ConfigureAwait(false);
        return new Batch(this, _open, context.Identity);
    }

    sealed class Batch(FitzKvProjectionStore store, IKvTransaction transaction, CheckpointIdentity identity)
        : IProjectionBatch
    {
        bool _done;

        public async ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            var tx = transaction;
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
                    throw FitzKvCheckpoints.Conflict("Projection batch", identity, ex);
                }
                else
                {
                    throw;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            var tx = transaction;
            if (!_done)
                await FitzKvCheckpoints.RollbackAsync(tx).ConfigureAwait(false);

            if (ReferenceEquals(store._open, tx))
                store._open = null;

            await tx.DisposeAsync().ConfigureAwait(false);
        }
    }
}

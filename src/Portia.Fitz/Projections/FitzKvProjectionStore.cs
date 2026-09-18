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
/// <remarks>
///     The configured route is a base, not the resource every batch transacts against. Fitz KV locks a
///     resource exclusively for a ReadWrite transaction's whole lifetime, so each workload — one
///     projector in one realm, and so each tenant of a per-tenant projector — gets its own derived
///     resource; two workloads can never contend for one lock however the routes are configured.
///     Query-side reads find that resource through <see cref="RouteFor" />.
/// </remarks>
/// <param name="client">The Fitz KV client to open transactions against.</param>
/// <param name="route">The Fitz KV base route this repository's per-workload resources derive from.</param>
public abstract class FitzKvProjectionStore(IKvClient client, string route) : IProjectionStore
{
    readonly IKvClient _client = client ?? throw new ArgumentNullException(nameof(client));

    readonly string _route = FitzKvCheckpoints.Route(route, nameof(route));

    IKvTransaction? _open;

    /// <summary>
    ///     Gets the Fitz KV resource one projector workload's data and checkpoint live in, so an
    ///     application's query-side reads — which run outside any batch — open their transaction where
    ///     the projector wrote.
    /// </summary>
    /// <param name="route">The base route the repository was constructed with.</param>
    /// <param name="componentName">The projector's registered workload name.</param>
    /// <param name="realm">
    ///     The realm the projector reads: the tenant ID for a <c>PerTenant</c> projector, otherwise
    ///     the realm of its <see cref="EventStreamPattern" />.
    /// </param>
    /// <returns>The derived <c>kv://{realm}/{area}/{resource}</c> route.</returns>
    /// <exception cref="ArgumentException">
    ///     The route is not an exact three-segment Fitz KV route, or the component name or realm is blank.
    /// </exception>
    public static string RouteFor(string route, string componentName, string realm)
    {
        var validated = FitzKvCheckpoints.Route(route, nameof(route));
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        return FitzKvCheckpoints.WorkloadRoute(validated, componentName, realm);
    }

    /// <summary>
    ///     Gets the transaction the current unit of work writes through. Only valid between
    ///     <see cref="BeginAsync" /> and the returned batch's commit or disposal — a derived
    ///     repository's own domain write methods use this so their writes land in the same atomic
    ///     commit as the checkpoint. Never open a second read-write transaction against this
    ///     workload's resource while this one is in flight; Fitz KV isolation conflicts, it does not
    ///     merge concurrent writers.
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
        return FitzKvCheckpoints.LoadAsync(_client, FitzKvCheckpoints.WorkloadRoute(_route, identity), identity, ct);
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

        var transaction = await FitzKvCheckpoints
            .BeginAsync(_client, FitzKvCheckpoints.WorkloadRoute(_route, context.Identity), KvMode.ReadWrite,
                "Projection batch", context.Identity, ct)
            .ConfigureAwait(false);
        _open = transaction;
        try
        {
            var checkpoint = await transaction.GetAsync(FitzKvCheckpoints.Key(context.Identity), ct)
                .ConfigureAwait(false);
            var current = checkpoint.Found
                ? new ProjectionCheckpoint(FitzKvCheckpoints.Decode(checkpoint.Value!.Value.Span))
                : ProjectionCheckpoint.Start;
            if (current != context.Checkpoint)
            {
                throw new ProjectionConcurrencyException(
                    $"Projection batch for '{context.Identity.ComponentName}' started from stale checkpoint "
                    + $"'{context.Checkpoint.Cursor}'; authoritative checkpoint is '{current.Cursor}'.");
            }

            return new Batch(this, transaction, context.Identity);
        }
        catch
        {
            if (ReferenceEquals(_open, transaction))
                _open = null;

            await FitzKvCheckpoints.RollbackAsync(transaction).ConfigureAwait(false);
            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve the checkpoint read, decode, cancellation, or validation failure.
            }

            throw;
        }
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
                await tx.PutAsync(FitzKvCheckpoints.Key(identity), FitzKvCheckpoints.Encode(checkpoint.Cursor), ct)
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

                throw;
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

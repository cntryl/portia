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
///     A repository serves the one projector it names at construction, so its query-side reads
///     derive that resource from the realm alone through <see cref="BeginReadAsync" />, and a
///     projector registered under any other name is rejected rather than writing where no read looks.
/// </remarks>
/// <param name="client">The Fitz KV client to open transactions against.</param>
/// <param name="route">The Fitz KV base route this repository's per-workload resources derive from.</param>
/// <param name="componentName">
///     The workload name of the projector this repository serves: the name it is registered under,
///     or, when the registration names none, the name the projector passes to its own constructor.
/// </param>
public abstract class FitzKvProjectionStore(IKvClient client, string route, string componentName) : IProjectionStore
{
    readonly IKvClient _client = client ?? throw new ArgumentNullException(nameof(client));

    readonly string _route = FitzKvCheckpoints.Route(route, nameof(route));

    readonly string _componentName = RequireName(componentName);

    IKvTransaction? _open;

    /// <summary>
    ///     Opens a read-only transaction on the resource this repository's projector writes for one
    ///     realm, so a query-side read — which runs outside any batch — sees what the projector
    ///     committed. It is read-only so a query never holds the write lock the projector needs.
    /// </summary>
    /// <param name="realm">
    ///     The realm to read: the tenant ID for a <c>PerTenant</c> projector, otherwise the realm of its
    ///     <see cref="EventStreamPattern" />.
    /// </param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A transaction the caller disposes when the read is finished.</returns>
    /// <exception cref="ArgumentException">The realm is blank.</exception>
    /// <exception cref="InvalidOperationException">
    ///     A batch is open on this store. This transaction would see only committed data, never the
    ///     batch's staged writes, so a write computed from it would silently lose updates; reads during a
    ///     batch go through <see cref="Transaction" />.
    /// </exception>
    protected async ValueTask<IKvTransaction> BeginReadAsync(string realm, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        if (_open is not null)
        {
            throw new InvalidOperationException(
                "A projection batch is open on this store, and a query-side read cannot see its staged writes. "
                + "Read through Transaction while a batch is open.");
        }

        return await _client
            .BeginAsync(FitzKvCheckpoints.WorkloadRoute(_route, _componentName, realm), KvDurability.Sync,
                KvMode.ReadOnly, ct)
            .ConfigureAwait(false);
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
        EnsureOwnProjector(identity);
        return FitzKvCheckpoints.LoadAsync(_client, FitzKvCheckpoints.WorkloadRoute(_route, identity), identity, ct);
    }

    /// <inheritdoc />
    public async ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        EnsureOwnProjector(context.Identity);
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

    static string RequireName(string componentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        return componentName;
    }

    // Reads derive their resource from this repository's projector name, so a batch for any other
    // component would commit where no read ever looks: the typical cause is a registration that names
    // the projector differently, or not at all, from the name given here.
    void EnsureOwnProjector(CheckpointIdentity identity)
    {
        if (!string.Equals(identity.ComponentName, _componentName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Repository '{GetType().FullName}' serves projector '{_componentName}' but was given workload "
                + $"'{identity.ComponentName}'. Register the projector under the name this repository is "
                + "constructed with, so its query-side reads find the data it writes.");
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

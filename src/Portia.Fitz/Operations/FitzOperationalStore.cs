using System.Text;
using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>
/// Persists operational evidence alongside the existing Fitz checkpoint keys. Every mutation uses
/// a short synchronous resource transaction; no application handler runs in these transactions.
/// </summary>
public sealed class FitzOperationalStore : IOperationalStore, IOperationalReactionCheckpointStore
{
    readonly IKvClient _client;
    readonly string _route;

    /// <summary>Creates a journal using the same base route as the quiesced legacy checkpoint store.</summary>
    public FitzOperationalStore(IKvClient client, string checkpointBaseRoute)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _route = FitzKvCheckpoints.Route(checkpointBaseRoute, nameof(checkpointBaseRoute));
    }

    /// <inheritdoc />
    public async ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default)
    {
        var identity = target.Identity;
        await using var tx = await _client.BeginAsync(FitzKvCheckpoints.WorkloadRoute(_route, identity),
            KvMode.ReadOnly, ct).ConfigureAwait(false);
        return await ReadStateAsync(tx, target, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        OperationalTarget[] targets;
        await using (var tx = await _client.BeginAsync(IndexRoute(scope), KvMode.ReadOnly, ct).ConfigureAwait(false))
        {
            var value = await tx.GetAsync(IndexKey(scope), ct).ConfigureAwait(false);
            targets = value.Found ? JsonSerializer.Deserialize(value.Value!.Value.Span,
                FitzJsonContext.Default.OperationalTargetArray)! : [];
        }
        var states = new List<OperationalProcessorState>();
        foreach (var target in targets.Where(t => t.Scope == scope))
        {
            if (await ReadAsync(target, ct).ConfigureAwait(false) is { } state)
                states.Add(state);
        }
        return states;
    }

    /// <inheritdoc />
    public async ValueTask<bool> CompareExchangeAsync(OperationalProcessorState replacement, long expectedRevision,
        Guid expectedOwner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateRevision(replacement, expectedRevision);
        var identity = replacement.Target.Identity;
        // A discoverable pointer is written first. A crash may leave a pointer to absent evidence,
        // which List ignores; it can never leave a running workload undiscoverable.
        if (expectedRevision == 0)
            await AddIndexAsync(replacement.Target, ct).ConfigureAwait(false);
        return await WriteAsync(identity, async tx =>
        {
            var actual = await ReadStateAsync(tx, replacement.Target, ct).ConfigureAwait(false);
            if (!Matches(actual, expectedRevision, expectedOwner))
                return false;
            await PutStateAsync(tx, replacement, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
        => FitzKvCheckpoints.LoadAsync(_client, FitzKvCheckpoints.WorkloadRoute(_route, identity), identity, ct);

    /// <summary>Rejects unfenced legacy writes after operational cutover.</summary>
    public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        => ValueTask.FromException(new InvalidOperationException(
            "Operational reactor progress must commit through its owning worker and completion receipt."));

    /// <inheritdoc />
    public async ValueTask<bool> CommitAsync(OperationalProcessorState replacement, long expectedRevision,
        Guid ownerSession, Guid attemptId, string? expectedCheckpoint, string? nextCheckpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateRevision(replacement, expectedRevision);
        var identity = replacement.Target.Identity;
        return await WriteAsync(identity, async tx =>
        {
            var actual = await ReadStateAsync(tx, replacement.Target, ct).ConfigureAwait(false);
            if (!Matches(actual, expectedRevision, ownerSession)
                || actual!.Attempts.SingleOrDefault(a => a.Id == attemptId) is not { Outcome: "running" } attempt
                || attempt.OwnerSession != ownerSession || attempt.ExpectedCheckpoint != expectedCheckpoint)
                return false;
            var stored = await tx.GetAsync(FitzKvCheckpoints.Key(identity), ct).ConfigureAwait(false);
            var checkpoint = stored.Found ? FitzKvCheckpoints.Decode(stored.Value!.Value.Span).Value : null;
            if (checkpoint != expectedCheckpoint)
                throw new OperationalCheckpointConflictException();
            var receipt = replacement.Attempts.Single(a => a.Id == attemptId);
            if (replacement.OwnerSession != ownerSession || replacement.Checkpoint != nextCheckpoint
                || receipt.Outcome != "committed" || receipt.NextCheckpoint != nextCheckpoint
                || receipt.OwnerSession != ownerSession || receipt.StartedAt != attempt.StartedAt
                || attempt.NextCheckpoint != nextCheckpoint
                || receipt.ExpectedCheckpoint != expectedCheckpoint || !receipt.Sources.SequenceEqual(attempt.Sources)
                || replacement.Settings != actual.Settings)
                throw new ArgumentException("A reactor commit requires its matching completion receipt.", nameof(replacement));
            await tx.PutAsync(FitzKvCheckpoints.Key(identity), FitzKvCheckpoints.Encode(
                nextCheckpoint is null ? EventCursor.Start : new EventCursor(nextCheckpoint)), ct).ConfigureAwait(false);
            await PutStateAsync(tx, replacement, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    static void ValidateRevision(OperationalProcessorState replacement, long expectedRevision)
    {
        if (replacement.Revision != checked(expectedRevision + 1))
            throw new ArgumentException("An operational transition must advance the revision exactly once.", nameof(replacement));
    }

    static bool Matches(OperationalProcessorState? actual, long revision, Guid owner)
        => actual is null ? revision == 0 && owner == Guid.Empty : actual.Revision == revision && actual.OwnerSession == owner;

    static ReadOnlyMemory<byte> Key(OperationalTarget target) => Encoding.UTF8.GetBytes(
        "operations-v1\0" + Encoding.UTF8.GetString(FitzKvCheckpoints.Key(target.Identity).Span)
        + "\0" + (target.Scope.Tenant is null ? "global" : "tenant:" + target.Scope.Tenant));

    static async ValueTask<OperationalProcessorState?> ReadStateAsync(IKvTransaction tx, OperationalTarget target,
        CancellationToken ct)
    {
        var value = await tx.GetAsync(Key(target), ct).ConfigureAwait(false);
        var state = value.Found ? JsonSerializer.Deserialize(value.Value!.Value.Span,
            FitzJsonContext.Default.OperationalProcessorState) : null;
        if (state is not null && state.Target != target)
            throw new InvalidDataException("The operational document does not match the requested target.");
        return state;
    }

    static async ValueTask PutStateAsync(IKvTransaction tx, OperationalProcessorState state, CancellationToken ct)
        => await tx.PutAsync(Key(state.Target), JsonSerializer.SerializeToUtf8Bytes(state,
            FitzJsonContext.Default.OperationalProcessorState), ct).ConfigureAwait(false);

    string IndexRoute(OperationalScope scope) => FitzKvCheckpoints.WorkloadRoute(_route,
        "portia-operations-index", scope.Tenant is null ? "global" : "tenant:" + scope.Tenant);

    static ReadOnlyMemory<byte> IndexKey(OperationalScope scope)
        => Encoding.UTF8.GetBytes("operations-index-v1\0" + (scope.Tenant is null ? "global" : "tenant:" + scope.Tenant));

    async ValueTask AddIndexAsync(OperationalTarget target, CancellationToken ct)
    {
        await using var tx = await _client.BeginAsync(IndexRoute(target.Scope), KvMode.ReadWrite, ct).ConfigureAwait(false);
        var value = await tx.GetAsync(IndexKey(target.Scope), ct).ConfigureAwait(false);
        var existing = value.Found ? JsonSerializer.Deserialize(value.Value!.Value.Span,
            FitzJsonContext.Default.OperationalTargetArray)! : [];
        if (existing.Contains(target))
            return;
        await tx.PutAsync(IndexKey(target.Scope), JsonSerializer.SerializeToUtf8Bytes(
            existing.Append(target).ToArray(), FitzJsonContext.Default.OperationalTargetArray), ct).ConfigureAwait(false);
        await tx.CommitAsync(KvDurability.Sync, ct).ConfigureAwait(false);
    }

    async ValueTask<bool> WriteAsync(CheckpointIdentity identity, Func<IKvTransaction, ValueTask<bool>> write,
        CancellationToken ct)
    {
        var tx = await FitzKvCheckpoints.BeginAsync(_client, FitzKvCheckpoints.WorkloadRoute(_route, identity),
            KvMode.ReadWrite, "Operations", identity, ct).ConfigureAwait(false);
        var committed = false;
        try
        {
            if (!await write(tx).ConfigureAwait(false))
                return false;
            await tx.CommitAsync(KvDurability.Sync, ct).ConfigureAwait(false);
            committed = true;
            return true;
        }
        catch
        {
            await FitzKvCheckpoints.RollbackAsync(tx).ConfigureAwait(false);
            throw;
        }
        finally
        {
            try
            { await tx.DisposeAsync().ConfigureAwait(false); }
            catch when (committed) { /* The receipt already proves the journal commit. */ }
        }
    }
}

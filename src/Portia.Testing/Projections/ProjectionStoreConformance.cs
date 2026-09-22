namespace Cntryl.Portia.Testing;

/// <summary>Reusable atomicity, concurrency, and generation-isolation checks for projection stores.</summary>
public static class ProjectionStoreConformance
{
    static readonly TimeSpan StaleOpenGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>Runs the complete projection-store conformance suite.</summary>
    /// <param name="probe">An isolated implementation adapter.</param>
    /// <param name="ct">A token that can cancel verification.</param>
    /// <returns>A task that completes when every check has passed.</returns>
    /// <exception cref="ConformanceViolationException">The implementation violates a persistence invariant.</exception>
    public static async ValueTask VerifyAsync(IProjectionStoreConformanceProbe probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ValidateIdentities(probe.LiveIdentity, probe.RebuildIdentity);
        await probe.ResetAsync(ct).ConfigureAwait(false);
        await VerifyDisposalRollbackAsync(probe, ct).ConfigureAwait(false);
        await VerifyAtomicCommitAsync(probe, ct).ConfigureAwait(false);
        await VerifyFailedCommitRollbackAsync(probe, ct).ConfigureAwait(false);
        await VerifyStaleCheckpointAsync(probe, ct).ConfigureAwait(false);
        await VerifyGenerationIsolationAsync(probe, ct).ConfigureAwait(false);
        await VerifyPatternIsolationAsync(probe, ct).ConfigureAwait(false);
    }

    static async ValueTask VerifyDisposalRollbackAsync(IProjectionStoreConformanceProbe probe, CancellationToken ct)
    {
        await using (var session = await probe.OpenSessionAsync(ct).ConfigureAwait(false))
        {
            var checkpoint = await session.Store.LoadCheckpointAsync(probe.LiveIdentity, ct).ConfigureAwait(false);
            await using var batch = await session.Store
                .BeginAsync(new ProjectionBatchContext(probe.LiveIdentity, checkpoint), ct)
                .ConfigureAwait(false);
            await session.StageValueAsync("discarded", ct).ConfigureAwait(false);
        }

        await RequireStateAsync(probe, probe.LiveIdentity, null, ProjectionCheckpoint.Start,
            "disposing an uncommitted batch", ct).ConfigureAwait(false);
    }

    static async ValueTask VerifyAtomicCommitAsync(IProjectionStoreConformanceProbe probe, CancellationToken ct)
    {
        await using (var session = await probe.OpenSessionAsync(ct).ConfigureAwait(false))
        {
            var checkpoint = await session.Store.LoadCheckpointAsync(probe.LiveIdentity, ct).ConfigureAwait(false);
            await using var batch = await session.Store
                .BeginAsync(new ProjectionBatchContext(probe.LiveIdentity, checkpoint), ct)
                .ConfigureAwait(false);
            await session.StageValueAsync("committed", ct).ConfigureAwait(false);
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")), ct).ConfigureAwait(false);
        }

        await RequireStateAsync(probe, probe.LiveIdentity, "committed", new ProjectionCheckpoint(new EventCursor("1")),
            "committing application data and progress", ct).ConfigureAwait(false);
    }

    static async ValueTask VerifyFailedCommitRollbackAsync(IProjectionStoreConformanceProbe probe, CancellationToken ct)
    {
        await using (var session = await probe.OpenSessionAsync(ct).ConfigureAwait(false))
        {
            var checkpoint = await session.Store.LoadCheckpointAsync(probe.LiveIdentity, ct).ConfigureAwait(false);
            await using var batch = await session.Store
                .BeginAsync(new ProjectionBatchContext(probe.LiveIdentity, checkpoint), ct)
                .ConfigureAwait(false);
            await session.StageValueAsync("must-not-commit", ct).ConfigureAwait(false);
            await session.FailNextCommitAsync(ct).ConfigureAwait(false);
            await RequireFailureAsync(
                () => batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("2")), ct),
                "The injected projection commit failure did not fail.").ConfigureAwait(false);
        }

        await RequireStateAsync(probe, probe.LiveIdentity, "committed", new ProjectionCheckpoint(new EventCursor("1")),
            "a failed atomic commit", ct).ConfigureAwait(false);
    }

    static async ValueTask VerifyStaleCheckpointAsync(IProjectionStoreConformanceProbe probe, CancellationToken ct)
    {
        await using var first = await probe.OpenSessionAsync(ct).ConfigureAwait(false);
        await using var stale = await probe.OpenSessionAsync(ct).ConfigureAwait(false);
        var checkpoint = await first.Store.LoadCheckpointAsync(probe.LiveIdentity, ct).ConfigureAwait(false);
        var staleCheckpoint = await stale.Store.LoadCheckpointAsync(probe.LiveIdentity, ct).ConfigureAwait(false);

        // The stale writer starts opening its batch before the winner commits, so a store that compares
        // the expected checkpoint only when a batch opens — and would let two overlapping batches both
        // commit — is caught. The open is not awaited before the winner commits: a store that locks the
        // resource at BEGIN, as Fitz KV does, makes it wait for the winner, and awaiting it here would
        // deadlock. A short grace lets an optimistic store finish opening before the winner commits.
        Task<IProjectionBatch>? staleOpen = null;
        var winnerCommitted = false;
        var firstBatch = await first.Store
            .BeginAsync(new ProjectionBatchContext(probe.LiveIdentity, checkpoint), ct)
            .ConfigureAwait(false);
        try
        {
            await first.StageValueAsync("winner", ct).ConfigureAwait(false);
            staleOpen = Task.Run(async () => await stale.Store
                .BeginAsync(new ProjectionBatchContext(probe.LiveIdentity, staleCheckpoint), ct)
                .ConfigureAwait(false), ct);
            _ = await Task.WhenAny(staleOpen, Task.Delay(StaleOpenGrace, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await firstBatch.CommitAsync(new ProjectionCheckpoint(new EventCursor("2")), ct).ConfigureAwait(false);
            winnerCommitted = true;
        }
        finally
        {
            await firstBatch.DisposeAsync().ConfigureAwait(false);
            if (!winnerCommitted && staleOpen is not null)
            {
                await DiscardAsync(staleOpen).ConfigureAwait(false);
            }
        }

        // A losing writer may be refused when it opens its batch — a store that locks the resource or
        // compares the checkpoint as it opens — or when it commits, for a store that compares the
        // checkpoint it read. Both are conformant, so the conflict is allowed to surface anywhere in the
        // losing sequence; what it must never do is succeed, or fail as anything but the shared exception.
        var conflict = await CaptureAsync(async () =>
        {
            await using var staleBatch = await staleOpen.ConfigureAwait(false);
            await stale.StageValueAsync("stale", ct).ConfigureAwait(false);
            await staleBatch.CommitAsync(new ProjectionCheckpoint(new EventCursor("2")), ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        RequireConcurrency(conflict, "A stale projection checkpoint was allowed to commit.");
        await RequireStateAsync(probe, probe.LiveIdentity, "winner", new ProjectionCheckpoint(new EventCursor("2")),
            "a stale checkpoint conflict", ct).ConfigureAwait(false);
    }

    // Releases a stale batch that finished opening only after the winner failed, so the verification
    // failure that is already propagating is not replaced by the stale batch's own outcome.
    static async ValueTask DiscardAsync(Task<IProjectionBatch> open)
    {
        try
        {
            var batch = await open.ConfigureAwait(false);
            await batch.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The winner's failure is the one worth reporting.
        }
    }

    static async ValueTask VerifyGenerationIsolationAsync(IProjectionStoreConformanceProbe probe, CancellationToken ct)
    {
        await using (var session = await probe.OpenSessionAsync(ct).ConfigureAwait(false))
        {
            var checkpoint = await session.Store.LoadCheckpointAsync(probe.RebuildIdentity, ct).ConfigureAwait(false);
            await using var batch = await session.Store
                .BeginAsync(new ProjectionBatchContext(probe.RebuildIdentity, checkpoint), ct)
                .ConfigureAwait(false);
            await session.StageValueAsync("rebuild", ct).ConfigureAwait(false);
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("7")), ct).ConfigureAwait(false);
        }

        await RequireStateAsync(probe, probe.LiveIdentity, "winner", new ProjectionCheckpoint(new EventCursor("2")),
            "writing a rebuild generation", ct).ConfigureAwait(false);
        await RequireStateAsync(probe, probe.RebuildIdentity, "rebuild", new ProjectionCheckpoint(new EventCursor("7")),
            "reloading a rebuild generation", ct).ConfigureAwait(false);
    }

    // The same component consuming another realm (another tenant) or another area is a separate
    // workload, so a store keyed by component and generation alone must fail here. The component
    // stays fixed: a store may legitimately be bound to the one projector it serves.
    static async ValueTask VerifyPatternIsolationAsync(IProjectionStoreConformanceProbe probe, CancellationToken ct)
    {
        var live = probe.LiveIdentity;
        CheckpointIdentity[] others =
        [
            new(live.ComponentName, EventStreamPattern.ForPattern(live.Pattern.Realm + "-other", live.Pattern.Area,
                live.Pattern.Resource)),
            new(live.ComponentName, EventStreamPattern.ForPattern(live.Pattern.Realm,
                (live.Pattern.Area ?? "area") + "-other", live.Pattern.Resource))
        ];

        foreach (var other in others)
        {
            await RequireStateAsync(probe, other, null, ProjectionCheckpoint.Start,
                $"loading independent identity {other.Pattern}", ct).ConfigureAwait(false);
            await using (var session = await probe.OpenSessionAsync(ct).ConfigureAwait(false))
            {
                await using var batch = await session.Store
                    .BeginAsync(new ProjectionBatchContext(other, ProjectionCheckpoint.Start), ct)
                    .ConfigureAwait(false);
                await session.StageValueAsync("other", ct).ConfigureAwait(false);
                await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("9")), ct).ConfigureAwait(false);
            }

            await RequireStateAsync(probe, live, "winner", new ProjectionCheckpoint(new EventCursor("2")),
                $"writing independent identity {other.Pattern}", ct).ConfigureAwait(false);
            await RequireStateAsync(probe, other, "other", new ProjectionCheckpoint(new EventCursor("9")),
                $"reloading independent identity {other.Pattern}", ct).ConfigureAwait(false);
        }
    }

    static async ValueTask RequireStateAsync(IProjectionStoreConformanceProbe probe, CheckpointIdentity identity,
        string? expectedValue, ProjectionCheckpoint expectedCheckpoint, string scenario, CancellationToken ct)
    {
        await using var session = await probe.OpenSessionAsync(ct).ConfigureAwait(false);
        var value = await session.ReadValueAsync(identity, ct).ConfigureAwait(false);
        var checkpoint = await session.Store.LoadCheckpointAsync(identity, ct).ConfigureAwait(false);
        if (!string.Equals(value, expectedValue, StringComparison.Ordinal) || checkpoint != expectedCheckpoint)
        {
            throw new ConformanceViolationException(
                $"After {scenario}, value/checkpoint were '{value ?? "<null>"}'/{checkpoint.Cursor}; expected '{expectedValue ?? "<null>"}'/{expectedCheckpoint.Cursor}.");
        }
    }

    static async ValueTask RequireFailureAsync(Func<ValueTask> action, string message)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return;
        }

        throw new ConformanceViolationException(message);
    }

    static async ValueTask<Exception?> CaptureAsync(Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    static void RequireConcurrency(Exception? conflict, string message)
    {
        if (conflict is null)
        {
            throw new ConformanceViolationException(message);
        }

        if (conflict is not ProjectionConcurrencyException)
        {
            throw new ConformanceViolationException(
                $"A stale projection commit threw '{conflict.GetType().FullName}'; it must throw {nameof(ProjectionConcurrencyException)} so one catch covers every adapter.");
        }
    }

    static void ValidateIdentities(CheckpointIdentity live, CheckpointIdentity rebuild)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(rebuild);
        if (live.RebuildId is not null || rebuild.RebuildId is null
                                       || live.ComponentName != rebuild.ComponentName ||
                                       live.Pattern != rebuild.Pattern)
        {
            throw new ArgumentException("The probe must provide matching live and rebuild identities.");
        }
    }
}

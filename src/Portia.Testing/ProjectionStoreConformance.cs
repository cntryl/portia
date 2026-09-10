namespace Cntryl.Portia.Testing;

/// <summary>Reusable atomicity, concurrency, and generation-isolation checks for projection stores.</summary>
public static class ProjectionStoreConformance
{
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
            await batch.CommitAsync(new ProjectionCheckpoint(1), ct).ConfigureAwait(false);
        }

        await RequireStateAsync(probe, probe.LiveIdentity, "committed", new ProjectionCheckpoint(1),
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
                () => batch.CommitAsync(new ProjectionCheckpoint(2), ct),
                "The injected projection commit failure did not fail.").ConfigureAwait(false);
        }

        await RequireStateAsync(probe, probe.LiveIdentity, "committed", new ProjectionCheckpoint(1),
            "a failed atomic commit", ct).ConfigureAwait(false);
    }

    static async ValueTask VerifyStaleCheckpointAsync(IProjectionStoreConformanceProbe probe, CancellationToken ct)
    {
        await using var first = await probe.OpenSessionAsync(ct).ConfigureAwait(false);
        await using var stale = await probe.OpenSessionAsync(ct).ConfigureAwait(false);
        var checkpoint = await first.Store.LoadCheckpointAsync(probe.LiveIdentity, ct).ConfigureAwait(false);
        var staleCheckpoint = await stale.Store.LoadCheckpointAsync(probe.LiveIdentity, ct).ConfigureAwait(false);
        await using var firstBatch = await first.Store
            .BeginAsync(new ProjectionBatchContext(probe.LiveIdentity, checkpoint), ct)
            .ConfigureAwait(false);
        await using var staleBatch = await stale.Store
            .BeginAsync(new ProjectionBatchContext(probe.LiveIdentity, staleCheckpoint), ct)
            .ConfigureAwait(false);
        await first.StageValueAsync("winner", ct).ConfigureAwait(false);
        await stale.StageValueAsync("stale", ct).ConfigureAwait(false);
        await firstBatch.CommitAsync(new ProjectionCheckpoint(2), ct).ConfigureAwait(false);
        await RequireConcurrencyFailureAsync(
            () => staleBatch.CommitAsync(new ProjectionCheckpoint(2), ct),
            "A stale projection checkpoint was allowed to commit.").ConfigureAwait(false);
        await RequireStateAsync(probe, probe.LiveIdentity, "winner", new ProjectionCheckpoint(2),
            "a stale checkpoint conflict", ct).ConfigureAwait(false);
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
            await batch.CommitAsync(new ProjectionCheckpoint(7), ct).ConfigureAwait(false);
        }

        await RequireStateAsync(probe, probe.LiveIdentity, "winner", new ProjectionCheckpoint(2),
            "writing a rebuild generation", ct).ConfigureAwait(false);
        await RequireStateAsync(probe, probe.RebuildIdentity, "rebuild", new ProjectionCheckpoint(7),
            "reloading a rebuild generation", ct).ConfigureAwait(false);
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
                $"After {scenario}, value/checkpoint were '{value ?? "<null>"}'/{checkpoint.NextOffset}; expected '{expectedValue ?? "<null>"}'/{expectedCheckpoint.NextOffset}.");
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

    static async ValueTask RequireConcurrencyFailureAsync(Func<ValueTask> action, string message)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProjectionConcurrencyException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new ConformanceViolationException(
                $"A stale projection commit threw '{ex.GetType().FullName}'; it must throw {nameof(ProjectionConcurrencyException)} so one catch covers every adapter.");
        }

        throw new ConformanceViolationException(message);
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

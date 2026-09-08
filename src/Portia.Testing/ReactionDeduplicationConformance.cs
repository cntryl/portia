namespace Cntryl.Portia;

/// <summary>Adapts an application's optional durable reaction deduplication primitive for verification.</summary>
public interface IReactionDeduplicationProbe
{
    /// <summary>Clears the isolated conformance state.</summary>
    ValueTask ResetAsync(CancellationToken ct = default);
    /// <summary>
    /// Executes <paramref name="effect" /> once for <paramref name="eventId" /> and returns
    /// <see langword="true" />, or returns <see langword="false" /> for a duplicate.
    /// </summary>
    ValueTask<bool> ExecuteAsync(Uuid eventId, Func<CancellationToken, ValueTask> effect,
        CancellationToken ct = default);
    /// <summary>Closes and reopens the durable implementation without clearing its state.</summary>
    ValueTask ReopenAsync(CancellationToken ct = default);
}

/// <summary>Reusable duplicate-event scenarios for an application's optional reaction guard.</summary>
public static class ReactionDeduplicationConformance
{
    /// <summary>
    /// Verifies sequential, concurrent, and post-reopen duplicate suppression. This does not
    /// prove crash atomicity between an external effect and bookkeeping.
    /// </summary>
    /// <param name="probe">An isolated durable deduplication adapter.</param>
    /// <param name="ct">A token that can cancel verification.</param>
    /// <exception cref="ConformanceViolationException">The implementation executes a duplicate.</exception>
    public static async ValueTask VerifyAsync(IReactionDeduplicationProbe probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        await probe.ResetAsync(ct).ConfigureAwait(false);
        var eventId = Uuid.CreateVersion4();
        var effects = 0;
        ValueTask Effect(CancellationToken unused)
        {
            _ = unused;
            _ = Interlocked.Increment(ref effects);
            return ValueTask.CompletedTask;
        }

        if (!await probe.ExecuteAsync(eventId, Effect, ct).ConfigureAwait(false))
            throw new ConformanceViolationException("The first reaction execution was reported as a duplicate.");
        if (await probe.ExecuteAsync(eventId, Effect, ct).ConfigureAwait(false) || effects != 1)
            throw new ConformanceViolationException("A sequential duplicate reaction executed more than once.");

        var concurrentId = Uuid.CreateVersion4();
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => probe.ExecuteAsync(concurrentId, Effect, ct).AsTask()).ToArray();
        _ = await Task.WhenAll(attempts).ConfigureAwait(false);
        if (attempts.Count(task => task.Result) != 1 || effects != 2)
            throw new ConformanceViolationException("A concurrent duplicate reaction executed more than once.");

        await probe.ReopenAsync(ct).ConfigureAwait(false);
        if (await probe.ExecuteAsync(eventId, Effect, ct).ConfigureAwait(false) || effects != 2)
            throw new ConformanceViolationException("A duplicate reaction executed after the implementation reopened.");
    }
}

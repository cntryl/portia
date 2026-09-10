namespace Cntryl.Portia.Testing;

/// <summary>Adapts an application's optional durable reaction deduplication primitive for verification.</summary>
public interface IReactionDeduplicationProbe
{
    /// <summary>Clears the isolated conformance state.</summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the state is empty.</returns>
    ValueTask ResetAsync(CancellationToken ct = default);

    /// <summary>
    ///     Executes <paramref name="effect" /> once for <paramref name="eventId" /> and returns
    ///     <see langword="true" />, or returns <see langword="false" /> for a duplicate.
    /// </summary>
    /// <param name="eventId">The source event the effect is deduplicated against.</param>
    /// <param name="effect">The effect to run at most once for that event.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns><see langword="true" /> when the effect ran, <see langword="false" /> for a duplicate.</returns>
    ValueTask<bool> ExecuteAsync(Uuid eventId, Func<CancellationToken, ValueTask> effect,
        CancellationToken ct = default);

    /// <summary>Closes and reopens the durable implementation without clearing its state.</summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the implementation is usable again.</returns>
    ValueTask ReopenAsync(CancellationToken ct = default);
}

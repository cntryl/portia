namespace Cntryl.Portia.Testing;

/// <summary>Adapts an application's optional durable reaction deduplication primitive for verification.</summary>
public interface IReactionDeduplicationProbe
{
    /// <summary>Clears the isolated conformance state.</summary>
    ValueTask ResetAsync(CancellationToken ct = default);

    /// <summary>
    ///     Executes <paramref name="effect" /> once for <paramref name="eventId" /> and returns
    ///     <see langword="true" />, or returns <see langword="false" /> for a duplicate.
    /// </summary>
    ValueTask<bool> ExecuteAsync(Uuid eventId, Func<CancellationToken, ValueTask> effect,
        CancellationToken ct = default);

    /// <summary>Closes and reopens the durable implementation without clearing its state.</summary>
    ValueTask ReopenAsync(CancellationToken ct = default);
}

namespace Cntryl.Portia;

/// <summary>
///     Hydrates aggregates from their event histories without the ability to persist them. Depend on this
///     from components that must not change state, such as request guards and authorizers.
/// </summary>
public interface IAggregateReader
{
    /// <summary>
    ///     Applies raised events after the aggregate's committed stream position and returns that same instance.
    ///     An absent stream leaves the instance unchanged. Construction and dependencies belong to the caller.
    ///     Save pending changes first. Do not use the instance concurrently during hydration.
    ///     Discard the instance if a domain event handler throws during replay.
    /// </summary>
    /// <typeparam name="TAggregate">The concrete aggregate type.</typeparam>
    /// <param name="aggregate">The caller-constructed aggregate.</param>
    /// <param name="ct">Cancels reading before replay begins.</param>
    /// <returns>The supplied instance, hydrated from its own stream address.</returns>
    ValueTask<TAggregate> HydrateAsync<TAggregate>(TAggregate aggregate, CancellationToken ct = default)
        where TAggregate : Aggregate;
}

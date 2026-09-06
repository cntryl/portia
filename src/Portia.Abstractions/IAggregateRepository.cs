namespace Cntryl.Portia;

/// <summary>
/// Loads and saves aggregates through their event histories.
/// </summary>
public interface IAggregateRepository
{
    /// <summary>
    /// Applies raised events after the aggregate's committed stream position and returns that same instance.
    /// An absent stream leaves the instance unchanged. Construction and dependencies belong to the caller.
    /// Save pending changes first. Do not use the instance concurrently during hydration.
    /// Discard the instance if a domain event handler throws during replay.
    /// </summary>
    /// <param name="aggregate">The caller-constructed aggregate.</param>
    /// <param name="ct">Cancels reading before replay begins.</param>
    /// <returns>The supplied instance, hydrated from its own stream address.</returns>
    ValueTask<TAggregate> HydrateAsync<TAggregate>(TAggregate aggregate, CancellationToken ct = default)
        where TAggregate : Aggregate;

    /// <summary>Stamps pending raised events or audits with execution attribution, frozen across save retries.</summary>
    ValueTask SaveAsync<TAggregate>(TAggregate aggregate, IExecutionContext context, CancellationToken ct = default)
        where TAggregate : Aggregate;
}

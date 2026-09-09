namespace Cntryl.Portia;

/// <summary>
/// Loads and saves aggregates through their event histories.
///
/// <para>Implemented by Portia. The invariants that make replay safe — version contiguity,
/// event-identity uniqueness, frozen save attribution, single-operation access — live in
/// <see cref="Aggregate" />'s internal surface, so this interface exists to be injected and
/// substituted with a test double, not to be reimplemented against a different store. To support
/// a new backing store, implement <see cref="IEventStore" /> instead; it is a complete public
/// contract, and <c>EventStoreConformance</c> in <c>Portia.Testing</c> verifies it.</para>
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

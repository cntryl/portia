namespace Cntryl.Portia;

/// <summary>
///     Loads and saves aggregates through their event histories.
///     <para>
///         Implemented by Portia. The invariants that make replay safe — version contiguity,
///         event-identity uniqueness, frozen save attribution, single-operation access — live in
///         <see cref="Aggregate" />'s internal surface, so this interface exists to be injected and
///         substituted with a test double, not to be reimplemented against a different store. To support
///         a new backing store, implement <see cref="IEventStore" /> instead; it is a complete public
///         contract, and <c>EventStoreConformance</c> in <c>Portia.Testing</c> verifies it.
///     </para>
///     <para>
///         Depend on <see cref="IAggregateReader" /> where a component must not persist, and on
///         <see cref="IAggregateWriter" /> where it only saves.
///     </para>
/// </summary>
public interface IAggregateRepository : IAggregateReader, IAggregateWriter;

namespace Cntryl.Portia.Testing;

/// <summary>The event the conformance suite appends. Applications never persist this type.</summary>
/// <param name="Sequence">The suite's own ordering marker.</param>
[Discriminator("portia.conformance.event")]
public sealed record ConformanceEvent(ulong Sequence) : DomainEvent;

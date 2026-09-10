namespace Cntryl.Portia.Consumer;

[Discriminator("Declined")]
public sealed record Declined(string Reason) : DomainEvent;

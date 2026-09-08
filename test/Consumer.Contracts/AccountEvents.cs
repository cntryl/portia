namespace Cntryl.Portia.Consumer;

[Discriminator("Deposited")]
public sealed record Deposited(int Amount) : DomainEvent;

[Discriminator("Declined")]
public sealed record Declined(string Reason) : DomainEvent;

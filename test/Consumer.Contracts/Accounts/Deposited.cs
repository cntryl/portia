namespace Cntryl.Portia.Consumer;

[Discriminator("Deposited")]
public sealed record Deposited(int Amount) : DomainEvent;

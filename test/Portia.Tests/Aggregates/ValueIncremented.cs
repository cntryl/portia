namespace Cntryl.Portia;

[Discriminator("test.value.incremented")]
sealed record ValueIncremented(int Amount) : DomainEvent;

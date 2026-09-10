namespace Cntryl.Portia;

[Discriminator("test.value.changed")]
sealed record ValueChanged(int Value) : DomainEvent;

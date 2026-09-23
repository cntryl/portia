namespace Cntryl.Portia;

[Discriminator("test.scenario.user-created")]
sealed record UserCreated(Uuid UserId) : DomainEvent;

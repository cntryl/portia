namespace Cntryl.Portia;

[Discriminator("test.event.unhandled")]
sealed record UnhandledEvent : DomainEvent;

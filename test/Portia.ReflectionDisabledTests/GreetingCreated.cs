namespace Cntryl.Portia.ReflectionDisabled;

[Discriminator("greetings.created")]
public sealed record GreetingCreated(string Name) : DomainEvent;

namespace Cntryl.Portia.ReflectionDisabled;

[RequestRoute("public", "greetings", "messages", "create")]
[Discriminator("greetings.create")]
public sealed record CreateGreeting(string Name) : IRequest<string>, ICallable;

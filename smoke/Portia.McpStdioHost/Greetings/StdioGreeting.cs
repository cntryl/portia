namespace Cntryl.Portia;

/// <summary>Reads a greeting and the explicitly configured stdio actor.</summary>
[Discriminator("stdio.greetings.read")]
sealed record StdioGreeting(string Name) : IRequest<string>, ICallable;

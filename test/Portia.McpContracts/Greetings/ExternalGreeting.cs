namespace Cntryl.Portia.McpContracts;

/// <summary>Reads a greeting defined in a separate contracts assembly.</summary>
/// <param name="Name">The person's name from the separate contracts assembly.</param>
[Discriminator("external.greetings.read")]
public sealed record ExternalGreeting(string Name) : IRequest<string>, ICallable;

[Discriminator("external.greetings.undocumented")]
public sealed record UndocumentedGreeting(string Name) : IRequest<string>, ICallable;

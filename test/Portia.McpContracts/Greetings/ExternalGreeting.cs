namespace Cntryl.Portia.McpContracts;

/// <summary>Reads a greeting defined in a separate contracts assembly.</summary>
[Discriminator("external.greetings.read")]
public sealed record ExternalGreeting(string Name) : IRequest<string>, ICallable;

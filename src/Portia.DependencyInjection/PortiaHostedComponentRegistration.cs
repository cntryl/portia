namespace Cntryl.Portia;

/// <summary>Identifies a component already activated through the low-level hosting API.</summary>
/// <param name="ComponentType">The concrete hosted component.</param>
public sealed record PortiaHostedComponentRegistration(Type ComponentType);

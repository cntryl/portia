namespace Cntryl.Portia;

/// <summary>Declares an application-owned, versioned wire discriminator independently of the CLR type name.</summary>
/// <param name="name">The non-empty logical name persisted or transmitted on the wire.</param>
/// <param name="version">The positive payload schema version.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DiscriminatorAttribute(string name, int version = 1) : Attribute
{
    /// <summary>Gets the payload schema version.</summary>
    public int Version { get; } = version > 0
        ? version
        : throw new ArgumentOutOfRangeException(nameof(version), "A discriminator version must be positive.");

    /// <summary>Gets the stable logical name.</summary>
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("A discriminator name cannot be empty.", nameof(name))
        : name;
}

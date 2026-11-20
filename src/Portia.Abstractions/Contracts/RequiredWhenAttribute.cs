namespace Cntryl.Portia;

/// <summary>Requires a nullable input member when another input member equals the specified string.</summary>
/// <param name="selector">The CLR name of a constructor-bound string member.</param>
/// <param name="value">The string that activates the requirement, compared ordinally.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class RequiredWhenAttribute(string selector, string value) : Attribute
{
    /// <summary>Gets the selector's CLR name.</summary>
    public string Selector { get; } = selector;

    /// <summary>Gets the string that activates the requirement.</summary>
    public string Value { get; } = value;
}

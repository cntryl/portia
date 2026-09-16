using System.ComponentModel;

namespace Cntryl.Portia;

/// <summary>Describes a generated request member to endpoint configuration.</summary>
/// <param name="name">The constructor parameter name.</param>
/// <param name="source">Where default binding looks first: <c>route</c>, <c>body</c>, or <c>query</c>.</param>
/// <param name="scalar">Whether the member can bind from text such as a query value.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PortiaHttpMember(string name, string source, bool scalar)
{
    /// <summary>Gets the constructor parameter name.</summary>
    public string Name { get; } = name;

    /// <summary>Gets where default binding looks first.</summary>
    public string Source { get; } = source;

    /// <summary>Gets whether the member can bind from text.</summary>
    public bool Scalar { get; } = scalar;
}

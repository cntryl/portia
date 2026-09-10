using System.ComponentModel;

namespace Cntryl.Portia;

/// <summary>One generated constructor binding in a Portia HTTP operation.</summary>
/// <param name="ClrName">The constructor parameter's declared name.</param>
/// <param name="Type">The parameter's CLR type.</param>
/// <param name="Source">Where the value binds from: the route, the query string, or the body.</param>
/// <param name="Required">Whether the caller must supply a value.</param>
/// <param name="HasDefault">Whether the parameter declares a default.</param>
/// <param name="DefaultValue">The declared default, when <paramref name="HasDefault" /> is set.</param>
/// <param name="WireName">
///     The name on the wire after the JSON naming policy is applied, or
///     <see langword="null" /> when it matches <paramref name="ClrName" />.
/// </param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record PortiaOpenApiParameter(
    string ClrName,
    Type Type,
    string Source,
    bool Required,
    bool HasDefault,
    object? DefaultValue,
    string? WireName);

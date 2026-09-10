using System.ComponentModel;

namespace Cntryl.Portia;

/// <summary>One generated constructor binding in a Portia HTTP operation.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record PortiaOpenApiParameter(
    string ClrName,
    Type Type,
    string Source,
    bool Required,
    bool HasDefault,
    object? DefaultValue,
    string? WireName);

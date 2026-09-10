using Microsoft.CodeAnalysis;

namespace Cntryl.Portia;

/// <summary>Identifies the Portia component interfaces inspected by architecture analyzers.</summary>
static class PortiaComponentRoles
{
    const string Namespace = "Cntryl.Portia";

    public static readonly string[] Handler =
        ["IRequestHandler`1", "IRequestHandler`2", "IStreamRequestHandler`2"];

    static readonly string[] Authorizer = ["IRequestAuthorizer`1"];

    static readonly string[] Behavior =
        ["IRequestPipelineBehavior`1", "IRequestPipelineBehavior`2", "IStreamRequestPipelineBehavior`2"];

    static readonly string[] All = [.. Handler, .. Authorizer, .. Behavior];

    public static bool Is(INamedTypeSymbol iface, string[] roles)
    {
        var definition = iface.OriginalDefinition;
        return definition.ContainingNamespace?.ToDisplayString() == Namespace
               && Array.IndexOf(roles, definition.MetadataName) >= 0;
    }

    public static bool IsComponent(INamedTypeSymbol symbol) =>
        symbol.AllInterfaces.Any(iface => Is(iface, All));
}

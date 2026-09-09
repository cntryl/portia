using Microsoft.CodeAnalysis;

namespace Cntryl.Portia;

/// <summary>
/// The single list of interfaces that make a type a Portia component. Every generator and
/// analyzer that asks "is this a Portia role?" reads it from here — the shape diagnostic, the
/// registration-name pool, and the call-site interceptor previously each kept their own copy,
/// and adding pipeline behaviors updated only one of the three.
/// </summary>
static class PortiaComponentRoles
{
    public const string Namespace = "Cntryl.Portia";

    /// <summary>Request handlers, including the streaming shape.</summary>
    public static readonly string[] Handler =
        ["IRequestHandler`1", "IRequestHandler`2", "IStreamRequestHandler`2"];

    /// <summary>Request authorizers.</summary>
    public static readonly string[] Authorizer = ["IRequestAuthorizer`1"];

    /// <summary>Request pipeline behaviors, including the streaming shape.</summary>
    public static readonly string[] Behavior =
        ["IRequestPipelineBehavior`1", "IRequestPipelineBehavior`2", "IStreamRequestPipelineBehavior`2"];

    /// <summary>Every role interface, for checks that apply to components generally.</summary>
    public static readonly string[] All = [.. Handler, .. Authorizer, .. Behavior];

    /// <summary>Whether an implemented interface is one of the supplied Portia roles.</summary>
    public static bool Is(INamedTypeSymbol iface, string[] roles)
    {
        var definition = iface.OriginalDefinition;
        return definition.ContainingNamespace?.ToDisplayString() == Namespace
            && Array.IndexOf(roles, definition.MetadataName) >= 0;
    }

    /// <summary>Whether a type implements any Portia component role.</summary>
    public static bool IsComponent(INamedTypeSymbol symbol) =>
        symbol.AllInterfaces.Any(iface => Is(iface, All));
}

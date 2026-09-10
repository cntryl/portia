using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Discovers component type names that contribute generated registration methods.</summary>
static class RegistrationComponentDiscovery
{
    /// <summary>Gets the fully qualified name of a handler, authorizer, or pipeline behavior.</summary>
    public static string? GetComponentTypeName(GeneratorSyntaxContext context)
        => context.Node is ClassDeclarationSyntax declaration
           && context.SemanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol { IsAbstract: false } symbol
           && GeneratedTypeShape.IsSupported(symbol)
           && PortiaComponentRoles.IsComponent(symbol)
            ? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Discovers component type names that contribute generated registration methods.</summary>
static class RegistrationComponentDiscovery
{
    public static string? GetHandlerOrAuthorizerTypeName(GeneratorSyntaxContext context)
        => context.Node is ClassDeclarationSyntax declaration
        && context.SemanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol { IsAbstract: false } symbol
        && GeneratedTypeShape.IsSupported(symbol)
        && symbol.AllInterfaces.Any(IsHandlerOrAuthorizer)
            ? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;

    static bool IsHandlerOrAuthorizer(INamedTypeSymbol iface) =>
        iface.OriginalDefinition.ContainingNamespace.ToDisplayString() == "Cntryl.Portia"
        && iface.OriginalDefinition.MetadataName is "IRequestHandler`1"
            or "IRequestHandler`2"
            or "IStreamRequestHandler`2"
            or "IRequestAuthorizer`1";
}

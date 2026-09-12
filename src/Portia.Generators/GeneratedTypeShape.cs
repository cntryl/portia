using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

static class GeneratedTypeShape
{
    public static readonly DiagnosticDescriptor Unsupported = new(
        "PORTIA015", "Unsupported component declaration",
        "Component '{0}' cannot be generated: {1}",
        "Portia", DiagnosticSeverity.Error, true);

    public static bool IsSupported(INamedTypeSymbol symbol, bool requirePartialContainers = false) =>
        UnsupportedReason(symbol, requirePartialContainers) is null;

    public static string? UnsupportedReason(INamedTypeSymbol symbol, bool requirePartialContainers = false)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType)
            {
                return SymbolEqualityComparer.Default.Equals(current, symbol)
                    ? "generic component types are unsupported; use a closed, non-generic component class"
                    : $"enclosing type '{current.ToDisplayString()}' is generic; move the component to a non-generic type";
            }

            if (current.DeclaredAccessibility is Accessibility.Private
                or Accessibility.Protected or Accessibility.ProtectedAndInternal)
            {
                return $"'{current.ToDisplayString()}' is not visible to generated code; make it internal or public";
            }

            if (requirePartialContainers && !SymbolEqualityComparer.Default.Equals(current, symbol)
                                             && current.DeclaringSyntaxReferences.Any(reference =>
                                                 reference.GetSyntax() is not TypeDeclarationSyntax declaration
                                                 || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                return $"enclosing type '{current.ToDisplayString()}' must be partial";
            }
        }

        return null;
    }

    public static string HintName(INamedTypeSymbol symbol)
    {
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(
            Encoding.UTF8.GetBytes(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        return symbol.Name + "_" + BitConverter.ToString(bytes).Replace("-", "");
    }
}

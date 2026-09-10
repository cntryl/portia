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
        "Component '{0}' must be accessible and non-generic; enclosing types of generated dispatchers must also be partial",
        "Portia", DiagnosticSeverity.Error, true);

    public static bool IsSupported(INamedTypeSymbol symbol, bool requirePartialContainers = false)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType || current.DeclaredAccessibility is Accessibility.Private
                    or Accessibility.Protected or Accessibility.ProtectedAndInternal)
            {
                return false;
            }

            if (requirePartialContainers && !SymbolEqualityComparer.Default.Equals(current, symbol)
                                             && current.DeclaringSyntaxReferences.Any(reference =>
                                                 reference.GetSyntax() is not TypeDeclarationSyntax declaration
                                                 || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                return false;
            }
        }

        return true;
    }

    public static string HintName(INamedTypeSymbol symbol)
    {
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(
            Encoding.UTF8.GetBytes(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        return symbol.Name + "_" + BitConverter.ToString(bytes).Replace("-", "");
    }
}

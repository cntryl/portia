using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

static class DiagnosticSuppression
{
    const string SuppressMessageAttributeMetadataName =
        "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute";

    public static bool IsSuppressed(
        GeneratorSyntaxContext context,
        TypeDeclarationSyntax declaration,
        INamedTypeSymbol symbol,
        string diagnosticId)
    {
        if (PragmaStateAt(declaration, diagnosticId) == ReportDiagnostic.Suppress)
        {
            return true;
        }

        return symbol.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == SuppressMessageAttributeMetadataName
            && attribute.ConstructorArguments.ElementAtOrDefault(1).Value is string checkId
            && string.Equals(checkId.Split(':')[0], diagnosticId, StringComparison.Ordinal));
    }

    static ReportDiagnostic PragmaStateAt(TypeDeclarationSyntax declaration, string diagnosticId)
    {
        var state = ReportDiagnostic.Default;
        foreach (var pragma in declaration.SyntaxTree.GetRoot().DescendantTrivia(descendIntoTrivia: true)
                     .Where(trivia => trivia.SpanStart < declaration.SpanStart)
                     .Select(static trivia => trivia.GetStructure())
                     .OfType<PragmaWarningDirectiveTriviaSyntax>()
                     .Where(static pragma => pragma.IsActive))
        {
            var ids = pragma.ErrorCodes.Select(static code => code.ToString());
            if (pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword))
            {
                if (!ids.Any() || ids.Contains(diagnosticId, StringComparer.Ordinal))
                    state = ReportDiagnostic.Suppress;
            }
            else if (!ids.Any() || ids.Contains(diagnosticId, StringComparer.Ordinal))
            {
                state = ReportDiagnostic.Default;
            }
        }

        return state;
    }
}

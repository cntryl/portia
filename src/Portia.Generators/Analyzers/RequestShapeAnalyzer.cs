using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia;

/// <summary>Reports unsupported component declarations before registration generation.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RequestShapeAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [GeneratedTypeShape.Unsupported];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        // Generated files are the application's code too: a JSON context or a partial component part often lives in
        // one, and skipping it would hide coverage or findings the generators always saw.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                               GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(static symbolContext =>
        {
            var symbol = (INamedTypeSymbol)symbolContext.Symbol;
            if (symbol is not { TypeKind: TypeKind.Class, IsRecord: false, IsAbstract: false }
                || !PortiaComponentRoles.IsComponent(symbol)
                || GeneratedTypeShape.UnsupportedReason(symbol) is not { } reason)
            {
                return;
            }

            symbolContext.ReportDiagnostic(Diagnostic.Create(GeneratedTypeShape.Unsupported,
                symbol.Locations.FirstOrDefault(), symbol.ToDisplayString(), reason));
        }, SymbolKind.NamedType);
    }
}

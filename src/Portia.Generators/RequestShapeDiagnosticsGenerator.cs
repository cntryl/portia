using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Reports unsupported component declarations before registration generation.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class RequestShapeDiagnosticsGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntaxContext, ct) =>
                syntaxContext.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)syntaxContext.Node, ct) as
                    INamedTypeSymbol);
        context.RegisterSourceOutput(types, static (output, symbol) =>
        {
            if (symbol is null || symbol.IsAbstract || GeneratedTypeShape.IsSupported(symbol))
            {
                return;
            }

            // Every role, from the shared list — a role added to the pipeline but not to this
            // check would silently lose its PORTIA015 diagnostic, which is what happened to
            // pipeline behaviors when they were introduced.
            if (PortiaComponentRoles.IsComponent(symbol))
            {
                output.ReportDiagnostic(Diagnostic.Create(GeneratedTypeShape.Unsupported,
                    symbol.Locations.FirstOrDefault(), symbol.ToDisplayString()));
            }
        });
    }
}

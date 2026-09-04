using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Reports unsupported handler and authorizer declarations before registration generation.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class RequestShapeDiagnosticsGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntaxContext, ct) => syntaxContext.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)syntaxContext.Node, ct) as INamedTypeSymbol);
        context.RegisterSourceOutput(types, static (output, symbol) =>
        {
            if (symbol is null || symbol.IsAbstract || GeneratedTypeShape.IsSupported(symbol))
                return;
            if (symbol.AllInterfaces.Any(iface => iface.ContainingNamespace.ToDisplayString() == "Cntryl.Portia"
                && (iface.Name == "IRequestHandler" || iface.Name == "IStreamRequestHandler" || iface.Name == "IRequestAuthorizer")))
            {
                output.ReportDiagnostic(Diagnostic.Create(GeneratedTypeShape.Unsupported, symbol.Locations.FirstOrDefault(), symbol.ToDisplayString()));
            }
        });
    }
}

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
            static (syntaxContext, ct) => Analyze(syntaxContext, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);
        context.RegisterSourceOutput(types, static (output, model) =>
        {
            output.ReportDiagnostic(Diagnostic.Create(GeneratedTypeShape.Unsupported,
                model.Location.ToLocation(), model.TypeName));
        });
    }

    static Model? Analyze(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var symbol = context.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)context.Node, ct) as
            INamedTypeSymbol;
        return symbol is null || symbol.IsAbstract || GeneratedTypeShape.IsSupported(symbol) ||
               !PortiaComponentRoles.IsComponent(symbol)
            ? null
            : new Model(symbol.ToDisplayString(), DiagnosticLocation.From(symbol.Locations.FirstOrDefault()));
    }

    sealed record Model(string TypeName, DiagnosticLocation Location);
}

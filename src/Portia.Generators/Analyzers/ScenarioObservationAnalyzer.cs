using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia;

/// <summary>
///     Reports a Portia testing scenario used as a statement and never awaited. Request scenarios run only when
///     awaited, so such a statement asserts nothing and the test passes regardless; outside an async method the
///     compiler gives no warning of its own.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScenarioObservationAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Unobserved = new("PORTIA107", "Test scenario is never awaited",
        "'{0}' is discarded without being awaited, so it runs nothing and asserts nothing. Await it.",
        "Portia", DiagnosticSeverity.Warning, true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Unobserved];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(static syntaxContext =>
        {
            if (Analyze(syntaxContext) is { } finding)
                syntaxContext.ReportDiagnostic(Diagnostic.Create(Unobserved, finding.Location, finding.TypeName));
        }, SyntaxKind.ExpressionStatement, SyntaxKind.ArrowExpressionClause);
    }

    // Only When and the Expect methods return a scenario; every other statement is left unbound.
    static bool MayBeScenario(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => string.Empty
        };
        return name == "When" || name.StartsWith("Expect", StringComparison.Ordinal);
    }

    static (Location Location, string TypeName)? Analyze(SyntaxNodeAnalysisContext context)
    {
        var expression = context.Node switch
        {
            ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation } statement
                when MayBeScenario(invocation) => statement.Expression,
            ArrowExpressionClauseSyntax { Expression: InvocationExpressionSyntax invocation } arrow
                when MayBeScenario(invocation) && ReturnsVoid(arrow, context.SemanticModel, context.CancellationToken)
                => arrow.Expression,
            _ => null
        };
        if (expression is null
            || context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type is not INamedTypeSymbol type
            || !SymbolNames.IsInNamespace(type, "Cntryl.Portia.Testing")
            || !type.GetMembers("GetAwaiter").OfType<IMethodSymbol>()
                .Any(method => method is { IsStatic: false, Parameters.Length: 0 }))
        {
            return null;
        }

        return (expression.GetLocation(), type.Name);
    }

    // An expression body discards its value only when the member it belongs to returns nothing.
    static bool ReturnsVoid(ArrowExpressionClauseSyntax arrow, SemanticModel model, CancellationToken ct) =>
        arrow.Parent is MethodDeclarationSyntax or LocalFunctionStatementSyntax
        && model.GetDeclaredSymbol(arrow.Parent, ct) is IMethodSymbol { ReturnsVoid: true };
}

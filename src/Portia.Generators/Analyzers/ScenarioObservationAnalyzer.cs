using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Cntryl.Portia;

/// <summary>
///     Reports discarded Portia request expectations, including immutable assertion augmentation.
///     Await or retain each returned expectation value so its assertions are observed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScenarioObservationAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Unobserved = new("PORTIA107", "Request scenario assertions are discarded",
        "'{0}' is discarded without observing its assertions. Await or retain the returned expectations.",
        "Portia", DiagnosticSeverity.Warning, true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Unobserved];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        // Generated files are the application's code too, and skipping them would hide findings the generators
        // always saw.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                               GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(static syntaxContext =>
            {
                if (Analyze(syntaxContext) is { } finding)
                    syntaxContext.ReportDiagnostic(Diagnostic.Create(Unobserved, finding.Location, finding.TypeName));
            }, SyntaxKind.ExpressionStatement, SyntaxKind.ArrowExpressionClause,
            SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression);
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
        var candidate = context.Node switch
        {
            ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
                when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) => assignment.Right,
            ExpressionStatementSyntax statement => statement.Expression,
            ArrowExpressionClauseSyntax arrow => arrow.Expression,
            LambdaExpressionSyntax { Body: ExpressionSyntax body } => body,
            _ => null
        };

        while (candidate is ParenthesizedExpressionSyntax parenthesized)
            candidate = parenthesized.Expression;

        if (candidate is not InvocationExpressionSyntax expression || !MayBeScenario(expression))
            return null;

        var isDiscarded = context.Node switch
        {
            ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
                when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) =>
                    context.SemanticModel.GetOperation(assignment.Left, context.CancellationToken) is IDiscardOperation,
            ExpressionStatementSyntax => true,
            ArrowExpressionClauseSyntax arrow =>
                ReturnsVoid(arrow, context.SemanticModel, context.CancellationToken),
            LambdaExpressionSyntax { Body: ExpressionSyntax } lambda =>
                context.SemanticModel.GetOperation(lambda, context.CancellationToken) is
                    IAnonymousFunctionOperation { Symbol.ReturnsVoid: true },
            _ => false
        };

        if (!isDiscarded)
            return null;

        if (context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type is not INamedTypeSymbol
                type
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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>
///     Reports a Portia testing scenario used as a statement and never awaited. Request scenarios run only when
///     awaited, so such a statement asserts nothing and the test passes regardless; outside an async method the
///     compiler gives no warning of its own.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ScenarioObservationGenerator : IIncrementalGenerator
{
    static readonly DiagnosticDescriptor Unobserved = new("PORTIA107", "Test scenario is never awaited",
        "'{0}' is discarded without being awaited, so it runs nothing and asserts nothing. Await it.",
        "Portia", DiagnosticSeverity.Warning, true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var findings = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node switch
                {
                    ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation } => MayBeScenario(invocation),
                    ArrowExpressionClauseSyntax { Expression: InvocationExpressionSyntax invocation } => MayBeScenario(invocation),
                    _ => false
                },
                static (syntaxContext, ct) => Analyze(syntaxContext, ct))
            .Where(static finding => finding is not null)
            .WithTrackingName("PortiaScenarioObservation");

        context.RegisterSourceOutput(findings, static (output, finding) =>
            output.ReportDiagnostic(Diagnostic.Create(Unobserved, Location.Create(finding!.Path,
                new TextSpan(finding.Start, finding.Length),
                new LinePositionSpan(new LinePosition(finding.Line, finding.Character),
                    new LinePosition(finding.EndLine, finding.EndCharacter))), finding.TypeName)));
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

    static Finding? Analyze(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var expression = context.Node switch
        {
            ExpressionStatementSyntax statement => statement.Expression,
            ArrowExpressionClauseSyntax arrow when ReturnsVoid(arrow, context.SemanticModel, ct) => arrow.Expression,
            _ => null
        };
        if (expression is null
            || context.SemanticModel.GetTypeInfo(expression, ct).Type is not INamedTypeSymbol type
            || type.ContainingNamespace?.ToDisplayString() != "Cntryl.Portia.Testing"
            || !type.GetMembers("GetAwaiter").OfType<IMethodSymbol>()
                .Any(method => method is { IsStatic: false, Parameters.Length: 0 }))
        {
            return null;
        }

        var span = expression.GetLocation().GetLineSpan().Span;
        return new Finding(expression.SyntaxTree.FilePath, expression.Span.Start, expression.Span.Length,
            span.Start.Line, span.Start.Character, span.End.Line, span.End.Character, type.Name);
    }

    // An expression body discards its value only when the member it belongs to returns nothing.
    static bool ReturnsVoid(ArrowExpressionClauseSyntax arrow, SemanticModel model, CancellationToken ct) =>
        arrow.Parent is MethodDeclarationSyntax or LocalFunctionStatementSyntax
        && model.GetDeclaredSymbol(arrow.Parent, ct) is IMethodSymbol { ReturnsVoid: true };

    // Incremental caching compares findings by value; netstandard2.0 has no records.
    sealed class Finding(string path, int start, int length, int line, int character, int endLine,
        int endCharacter, string typeName) : IEquatable<Finding>
    {
        public string Path { get; } = path;
        public int Start { get; } = start;
        public int Length { get; } = length;
        public int Line { get; } = line;
        public int Character { get; } = character;
        public int EndLine { get; } = endLine;
        public int EndCharacter { get; } = endCharacter;
        public string TypeName { get; } = typeName;

        public bool Equals(Finding? other) => other is not null && Path == other.Path && Start == other.Start
                                              && Length == other.Length && Line == other.Line
                                              && Character == other.Character && EndLine == other.EndLine
                                              && EndCharacter == other.EndCharacter && TypeName == other.TypeName;

        public override bool Equals(object? obj) => Equals(obj as Finding);
        public override int GetHashCode() => (Path, Start, Length).GetHashCode();
    }
}

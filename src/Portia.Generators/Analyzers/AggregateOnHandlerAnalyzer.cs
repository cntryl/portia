using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia;

/// <summary>Warns when event handlers contain business decisions that can invalidate replay.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AggregateOnHandlerAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor BusinessLogic = new("PORTIA108", "Aggregate event handler contains business logic",
        "On<TEvent> handler contains a throw or Result-returning call. Move business validation and failure decisions to the command that raises the event.",
        "Portia", DiagnosticSeverity.Warning, true, customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [BusinessLogic];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                               GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            var aggregateType = startContext.Compilation.GetTypeByMetadataName("Cntryl.Portia.Aggregate");
            var expectedMethod = aggregateType?.GetMembers("On").OfType<IMethodSymbol>()
                .FirstOrDefault(static candidate => candidate.TypeParameters.Length == 1);
            if (expectedMethod is null)
                return;

            var resultType = startContext.Compilation.GetTypeByMetadataName("Cntryl.Portia.Result");
            var genericResultType = startContext.Compilation.GetTypeByMetadataName("Cntryl.Portia.Result`1");
            var registeredHandlers = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            var findings = new ConcurrentDictionary<IMethodSymbol, Location>(SymbolEqualityComparer.Default);
            startContext.RegisterSyntaxNodeAction(syntaxContext => AnalyzeRegistration(syntaxContext, expectedMethod,
                    resultType, genericResultType, registeredHandlers), SyntaxKind.InvocationExpression);
            startContext.RegisterSyntaxNodeAction(syntaxContext => AnalyzeMethod(syntaxContext, resultType,
                genericResultType, findings), SyntaxKind.MethodDeclaration);
            startContext.RegisterCompilationEndAction(endContext =>
            {
                foreach (var handler in registeredHandlers.Keys)
                {
                    if (findings.TryGetValue(handler, out var location))
                        endContext.ReportDiagnostic(Diagnostic.Create(BusinessLogic, location));
                }
            });
        });
    }

    static void AnalyzeRegistration(SyntaxNodeAnalysisContext context, IMethodSymbol expectedMethod,
        INamedTypeSymbol? resultType, INamedTypeSymbol? genericResultType,
        ConcurrentDictionary<IMethodSymbol, byte> registeredHandlers)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            SimpleNameSyntax simpleName => simpleName.Identifier.ValueText,
            _ => string.Empty
        };
        if (name != "On" || invocation.ArgumentList.Arguments.Count != 1
            || context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
                method
            || !SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, expectedMethod.OriginalDefinition))
            return;

        var argument = invocation.ArgumentList.Arguments[0].Expression;
        while (argument is ParenthesizedExpressionSyntax parenthesized)
            argument = parenthesized.Expression;

        SyntaxNode? handlerBody;
        if (argument is LambdaExpressionSyntax lambda)
        {
            handlerBody = lambda.Body;
        }
        else if (argument is AnonymousMethodExpressionSyntax anonymousMethod)
        {
            handlerBody = anonymousMethod.Block;
        }
        else if (context.SemanticModel.GetSymbolInfo(argument, context.CancellationToken).Symbol is IMethodSymbol
                 handler && handler.DeclaringSyntaxReferences.Length > 0
                 && handler.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken) is
                     MethodDeclarationSyntax)
        {
            registeredHandlers.TryAdd(handler, 0);
            return;
        }
        else
        {
            return;
        }

        if (handlerBody is null)
            return;

        if (FindDecision(handlerBody, context.SemanticModel, resultType, genericResultType,
                context.CancellationToken) is { } decision)
            context.ReportDiagnostic(Diagnostic.Create(BusinessLogic, decision.GetLocation()));
    }

    static void AnalyzeMethod(SyntaxNodeAnalysisContext context, INamedTypeSymbol? resultType,
        INamedTypeSymbol? genericResultType, ConcurrentDictionary<IMethodSymbol, Location> findings)
    {
        var syntax = (MethodDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not IMethodSymbol method
            || syntax.Body is null && syntax.ExpressionBody is null)
            return;
        var handlerBody = (SyntaxNode?)syntax.Body ?? syntax.ExpressionBody!.Expression;
        if (FindDecision(handlerBody, context.SemanticModel, resultType, genericResultType,
                context.CancellationToken) is { } decision)
            findings.TryAdd(method, decision.GetLocation());
    }

    static SyntaxNode? FindDecision(SyntaxNode body, SemanticModel semanticModel, INamedTypeSymbol? resultType,
        INamedTypeSymbol? genericResultType, CancellationToken cancellationToken)
    {
        foreach (var node in body.DescendantNodesAndSelf(descendIntoChildren: child =>
                     child == body || child is not AnonymousFunctionExpressionSyntax
                         and not LocalFunctionStatementSyntax))
        {
            var hasDecision = node is ThrowStatementSyntax or ThrowExpressionSyntax
                              || node is InvocationExpressionSyntax call
                              && semanticModel.GetSymbolInfo(call, cancellationToken).Symbol is
                                  IMethodSymbol calledMethod
                              && ReturnsResult(calledMethod.ReturnType, resultType, genericResultType);
            if (!hasDecision)
                continue;

            return node;
        }
        return null;
    }

    static bool ReturnsResult(ITypeSymbol type, INamedTypeSymbol? result, INamedTypeSymbol? genericResult)
    {
        if (type is not INamedTypeSymbol named)
            return false;
        var definition = named.OriginalDefinition;
        return result is not null && SymbolEqualityComparer.Default.Equals(definition, result)
               || genericResult is not null && SymbolEqualityComparer.Default.Equals(definition, genericResult);
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>
/// Reports application-design mistakes this architecture makes easy and nothing else catches.
/// These are warnings, not errors: each one restates a boundary Portia's own documentation
/// already asserts, and an application that means to cross one can suppress it deliberately.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ComponentPracticeGenerator : IIncrementalGenerator
{
    static readonly DiagnosticDescriptor ProjectorEffect = new("PORTIA100", "Projector must not cause external effects",
        "Projector '{0}' takes '{1}'. A projector is a pure function of events into its own store — its writes and checkpoint commit atomically, so any effect it causes replays on every commit failure and every rebuild. Move the effect to a reactor.",
        "Portia", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    static readonly DiagnosticDescriptor ServiceLocation = new("PORTIA101", "Component resolves services at runtime",
        "'{0}' takes '{1}'. Portia components declare what they need as constructor dependencies so the whole graph stays visible to the compiler and to trimming; resolving from the container hides it.",
        "Portia", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    static readonly DiagnosticDescriptor AggregateService = new("PORTIA102", "Aggregate depends on a service",
        "Aggregate '{0}' takes '{1}'. An aggregate receives data and an optional IDomainEventMetadataFactory; loading and persisting are the repository's job, and a service dependency makes the aggregate impossible to replay in isolation.",
        "Portia", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    static readonly DiagnosticDescriptor MultipleHandlers = new("PORTIA103", "Type handles more than one request",
        "'{0}' implements {1} request handler interfaces. The request and its handler are Portia's unit of responsibility; split them so each request's behavior can change on its own.",
        "Portia", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    static readonly DiagnosticDescriptor CaughtExceptionAsResult = new("PORTIA104", "Unexpected failure converted to a Result",
        "'{0}' catches Exception and returns a failed Result. Result describes failures a handler anticipates; an unrecognized failure must stay an exception so transports can tell 'this request is invalid' from 'this call broke'.",
        "Portia", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    // Anything that reaches outside the component's own unit of work.
    static readonly string[] EffectTypes =
    [
        "Cntryl.Portia.IRequestBus",
        "Cntryl.Portia.IRemoteRequestSender",
        "Cntryl.Portia.IRequestQueuePublisher",
        "Cntryl.Portia.INoticeRequestSender",
        "Cntryl.Portia.IRequestScheduler",
        "Cntryl.Portia.IAggregateRepository",
        "System.Net.Http.HttpClient",
    ];

    static readonly string[] LocatorTypes =
    [
        "System.IServiceProvider",
        "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
    ];

    static readonly string[] AggregateForbiddenTypes =
    [
        .. EffectTypes,
        .. LocatorTypes,
        "Cntryl.Portia.IEventStore",
        "Cntryl.Portia.IDomainEventReader",
        "Cntryl.Portia.IDomainEventWriter",
        "Cntryl.Portia.IProjectionStore",
        "Cntryl.Portia.IProjectionCheckpointStore",
    ];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
            static (syntaxContext, ct) => (Symbol: syntaxContext.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)syntaxContext.Node, ct),
                Node: (TypeDeclarationSyntax)syntaxContext.Node));

        context.RegisterSourceOutput(types.Combine(context.CompilationProvider), static (output, pair) =>
        {
            var (type, compilation) = pair;
            if (type.Symbol is not { IsAbstract: false } symbol)
                return;
            ReportProjectorEffects(output, symbol);
            ReportServiceLocation(output, symbol);
            ReportAggregateServices(output, symbol);
            ReportMultipleHandlers(output, symbol);
            ReportCaughtExceptionAsResult(output, symbol, type.Node, compilation);
        });
    }

    static void ReportProjectorEffects(SourceProductionContext output, INamedTypeSymbol symbol)
    {
        if (!DerivesFrom(symbol, "Cntryl.Portia.BaseProjector"))
            return;
        foreach (var parameter in Parameters(symbol, EffectTypes))
            output.ReportDiagnostic(Diagnostic.Create(ProjectorEffect, Location(parameter), symbol.Name, Display(parameter)));
    }

    static void ReportServiceLocation(SourceProductionContext output, INamedTypeSymbol symbol)
    {
        if (!IsPortiaComponent(symbol))
            return;
        foreach (var parameter in Parameters(symbol, LocatorTypes))
            output.ReportDiagnostic(Diagnostic.Create(ServiceLocation, Location(parameter), symbol.Name, Display(parameter)));
    }

    static void ReportAggregateServices(SourceProductionContext output, INamedTypeSymbol symbol)
    {
        if (!DerivesFrom(symbol, "Cntryl.Portia.Aggregate"))
            return;
        foreach (var parameter in Parameters(symbol, AggregateForbiddenTypes))
            output.ReportDiagnostic(Diagnostic.Create(AggregateService, Location(parameter), symbol.Name, Display(parameter)));
    }

    static void ReportMultipleHandlers(SourceProductionContext output, INamedTypeSymbol symbol)
    {
        var handlers = symbol.AllInterfaces.Count(iface => PortiaComponentRoles.Is(iface, PortiaComponentRoles.Handler));
        if (handlers > 1)
            output.ReportDiagnostic(Diagnostic.Create(MultipleHandlers, symbol.Locations.FirstOrDefault(), symbol.Name, handlers));
    }

    static void ReportCaughtExceptionAsResult(
        SourceProductionContext output,
        INamedTypeSymbol symbol,
        TypeDeclarationSyntax node,
        Compilation compilation)
    {
        var exceptionType = compilation.GetTypeByMetadataName("System.Exception");
        var resultType = compilation.GetTypeByMetadataName("Cntryl.Portia.Result");
        var genericResultType = compilation.GetTypeByMetadataName("Cntryl.Portia.Result`1");
        if (exceptionType is null || resultType is null || genericResultType is null)
            return;

        var implementations = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var handler in symbol.AllInterfaces.Where(iface => PortiaComponentRoles.Is(iface, PortiaComponentRoles.Handler)))
        {
            foreach (var contractMethod in handler.GetMembers("HandleAsync").OfType<IMethodSymbol>())
            {
                var implementation = symbol.FindImplementationForInterfaceMember(contractMethod);
                if (implementation is IMethodSymbol { IsAbstract: false } method
                    && SymbolEqualityComparer.Default.Equals(method.ContainingType, symbol))
                {
                    _ = implementations.Add(method);
                }
            }
        }

        foreach (var implementation in implementations.OfType<IMethodSymbol>())
        {
            foreach (var syntaxReference in implementation.DeclaringSyntaxReferences)
            {
                if (syntaxReference.GetSyntax(output.CancellationToken) is not MethodDeclarationSyntax method
                    || method.SyntaxTree != node.SyntaxTree
                    || !method.Ancestors().Contains(node))
                {
                    continue;
                }

                var semanticModel = compilation.GetSemanticModel(method.SyntaxTree);
                foreach (var clause in method.DescendantNodes(DescendIntoHandlerBody).OfType<CatchClauseSyntax>())
                {
                    // A bare catch, or one naming System.Exception itself — a specific exception
                    // type is a failure the handler genuinely anticipates, which is what Result is for.
                    if (clause.Declaration?.Type is { } caught
                        && !SymbolEqualityComparer.Default.Equals(
                            semanticModel.GetTypeInfo(caught, output.CancellationToken).Type,
                            exceptionType))
                    {
                        continue;
                    }

                    if (!clause.Block.DescendantNodes(DescendIntoCatchBlock).OfType<InvocationExpressionSyntax>()
                        .Any(invocation => IsResultFailure(
                            semanticModel.GetSymbolInfo(invocation, output.CancellationToken).Symbol as IMethodSymbol,
                            resultType,
                            genericResultType)))
                    {
                        continue;
                    }

                    output.ReportDiagnostic(Diagnostic.Create(CaughtExceptionAsResult, clause.GetLocation(), symbol.Name));
                }
            }
        }

        static bool DescendIntoHandlerBody(SyntaxNode current)
        {
            return current is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        }

        static bool DescendIntoCatchBlock(SyntaxNode current)
        {
            return current is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        }

        static bool IsResultFailure(
            IMethodSymbol? method,
            INamedTypeSymbol result,
            INamedTypeSymbol genericResult)
        {
            if (method is not { Name: "Failure", IsStatic: true })
                return false;

            var containingType = method.ContainingType.OriginalDefinition;
            return SymbolEqualityComparer.Default.Equals(containingType, result)
                || SymbolEqualityComparer.Default.Equals(containingType, genericResult);
        }
    }

    static bool IsPortiaComponent(INamedTypeSymbol symbol) =>
        PortiaComponentRoles.IsComponent(symbol)
        || DerivesFrom(symbol, "Cntryl.Portia.BaseProjector")
        || DerivesFrom(symbol, "Cntryl.Portia.BaseReactor")
        || DerivesFrom(symbol, "Cntryl.Portia.Aggregate");

    static bool DerivesFrom(INamedTypeSymbol symbol, string baseTypeName)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString() == baseTypeName)
                return true;
        }
        return false;
    }

    static IEnumerable<IParameterSymbol> Parameters(INamedTypeSymbol symbol, string[] forbidden) =>
        symbol.InstanceConstructors
            .Where(constructor => !constructor.IsImplicitlyDeclared)
            .SelectMany(constructor => constructor.Parameters)
            .Where(parameter => Array.IndexOf(forbidden, parameter.Type.OriginalDefinition.ToDisplayString()) >= 0);

    static Location? Location(IParameterSymbol parameter) => parameter.Locations.FirstOrDefault();

    static string Display(IParameterSymbol parameter) => parameter.Type.Name;
}

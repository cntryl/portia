using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>
///     Reports application-design mistakes this architecture makes easy and nothing else catches.
///     These are warnings, not errors: each one restates a boundary Portia's own documentation
///     already asserts, and an application that means to cross one can suppress it deliberately.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ComponentPracticeGenerator : IIncrementalGenerator
{
    static readonly DiagnosticDescriptor ProjectorEffect = new("PORTIA100", "Projector has known effect dependency",
        "Projector '{0}' takes known effect dependency '{1}'. Effects can replay after commit failure or during rebuild; move the effect to a reactor.",
        "Portia", DiagnosticSeverity.Warning, true);

    static readonly DiagnosticDescriptor ServiceLocation = new("PORTIA101", "Component uses service location",
        "'{0}' uses service location through '{1}'. Declare dependencies directly so the component graph remains visible.",
        "Portia", DiagnosticSeverity.Warning, true);

    static readonly DiagnosticDescriptor AggregateService = new("PORTIA102", "Aggregate depends on a service",
        "Aggregate '{0}' takes '{1}'. An aggregate receives data and an optional IDomainEventMetadataFactory; loading and persisting are the repository's job, and a service dependency makes the aggregate impossible to replay in isolation.",
        "Portia", DiagnosticSeverity.Warning, true);

    static readonly DiagnosticDescriptor MultipleHandlers = new("PORTIA103", "Type handles more than one request",
        "'{0}' implements {1} request handler interfaces. The request and its handler are Portia's unit of responsibility; split them so each request's behavior can change on its own.",
        "Portia", DiagnosticSeverity.Warning, true);

    static readonly DiagnosticDescriptor CaughtExceptionAsResult = new("PORTIA104",
        "Unexpected failure converted to a Result",
        "'{0}' catches Exception and returns a failed Result. Result describes failures a handler anticipates; an unrecognized failure must stay an exception so transports can tell 'this request is invalid' from 'this call broke'.",
        "Portia", DiagnosticSeverity.Warning, true);

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
        "System.Net.Http.IHttpClientFactory",
        "System.Net.Mail.SmtpClient",
        "Stripe.IStripeClient",
        "Microsoft.EntityFrameworkCore.DbContext",
        "System.Data.Common.DbConnection",
        "Grpc.Core.ClientBase`1"
    ];

    static readonly string[] LocatorTypes =
    [
        "System.IServiceProvider",
        "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
        "Microsoft.Extensions.DependencyInjection.IServiceScope"
    ];

    static readonly string[] AggregateForbiddenTypes =
    [
        .. EffectTypes,
        .. LocatorTypes,
        "Cntryl.Portia.IEventStore",
        "Cntryl.Portia.IDomainEventReader",
        "Cntryl.Portia.IDomainEventWriter",
        "Cntryl.Portia.IProjectionStore",
        "Cntryl.Portia.IProjectionCheckpointStore"
    ];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
            static (syntaxContext, ct) => Analyze(syntaxContext, ct));

        context.RegisterSourceOutput(types, static (output, findings) =>
        {
            foreach (var finding in findings)
                output.ReportDiagnostic(Diagnostic.Create(Descriptor(finding.Kind), finding.Location.ToLocation(),
                    finding.Arguments));
        });
    }

    static Finding[] Analyze(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var node = (TypeDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(node, ct) is not { IsAbstract: false } symbol)
            return [];
        var findings = new List<Finding>();
        ReportProjectorEffects(findings, symbol);
        ReportServiceLocation(findings, symbol, node, context.SemanticModel);
        ReportAggregateServices(findings, symbol);
        ReportMultipleHandlers(findings, symbol);
        ReportCaughtExceptionAsResult(findings, symbol, node, context.SemanticModel, ct);
        return [.. findings];
    }

    static void ReportProjectorEffects(List<Finding> findings, INamedTypeSymbol symbol)
    {
        if (!DerivesFrom(symbol, "Cntryl.Portia.Projector"))
            return;
        foreach (var parameter in Parameters(symbol, EffectTypes))
        {
            findings.Add(Finding.Create(Kind.ProjectorEffect, Location(parameter), symbol.Name, Display(parameter)));
        }
    }

    static void ReportServiceLocation(
        List<Finding> findings,
        INamedTypeSymbol symbol,
        TypeDeclarationSyntax node,
        SemanticModel semanticModel)
    {
        if (!IsPortiaComponent(symbol))
            return;
        foreach (var parameter in Parameters(symbol, LocatorTypes))
        {
            findings.Add(Finding.Create(Kind.ServiceLocation, Location(parameter), symbol.Name, Display(parameter)));
        }

        foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>()
                     .Where(candidate => candidate.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() == node))
        {
            if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
                MetadataName(method.ContainingType) != "Microsoft.Extensions.DependencyInjection.ActivatorUtilities")
            {
                continue;
            }

            findings.Add(Finding.Create(Kind.ServiceLocation, invocation.GetLocation(), symbol.Name,
                $"ActivatorUtilities.{method.Name}"));
        }
    }

    static void ReportAggregateServices(List<Finding> findings, INamedTypeSymbol symbol)
    {
        if (!DerivesFrom(symbol, "Cntryl.Portia.Aggregate"))
            return;
        foreach (var parameter in Parameters(symbol, AggregateForbiddenTypes))
        {
            findings.Add(Finding.Create(Kind.AggregateService, Location(parameter), symbol.Name, Display(parameter)));
        }
    }

    static void ReportMultipleHandlers(List<Finding> findings, INamedTypeSymbol symbol)
    {
        var handlers =
            symbol.AllInterfaces.Count(iface => PortiaComponentRoles.Is(iface, PortiaComponentRoles.Handler));
        if (handlers > 1)
        {
            findings.Add(Finding.Create(Kind.MultipleHandlers, symbol.Locations.FirstOrDefault(), symbol.Name,
                handlers));
        }
    }

    static void ReportCaughtExceptionAsResult(
        List<Finding> findings,
        INamedTypeSymbol symbol,
        TypeDeclarationSyntax node,
        SemanticModel semanticModel,
        CancellationToken ct)
    {
        var compilation = semanticModel.Compilation;
        var exceptionType = compilation.GetTypeByMetadataName("System.Exception");
        var resultType = compilation.GetTypeByMetadataName("Cntryl.Portia.Result");
        var genericResultType = compilation.GetTypeByMetadataName("Cntryl.Portia.Result`1");
        if (exceptionType is null || resultType is null || genericResultType is null)
            return;

        var implementations = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var handler in symbol.AllInterfaces.Where(iface =>
                     PortiaComponentRoles.Is(iface, PortiaComponentRoles.Handler)))
        {
            foreach (var contractMethod in handler.GetMembers("HandleAsync").OfType<IMethodSymbol>())
            {
                var implementation = symbol.FindImplementationForInterfaceMember(contractMethod);
                if (implementation is IMethodSymbol { IsAbstract: false } method &&
                    SymbolEqualityComparer.Default.Equals(method.ContainingType, symbol))
                {
                    _ = implementations.Add(method);
                }
            }
        }

        foreach (var implementation in implementations.OfType<IMethodSymbol>())
        {
            foreach (var syntaxReference in implementation.DeclaringSyntaxReferences)
            {
                if (syntaxReference.GetSyntax(ct) is not MethodDeclarationSyntax method
                    || method.SyntaxTree != node.SyntaxTree
                    || !method.Ancestors().Contains(node))
                {
                    continue;
                }

                foreach (var clause in method.DescendantNodes(DescendIntoHandlerBody).OfType<CatchClauseSyntax>())
                {
                    if (clause.Declaration?.Type is
                        // A bare catch, or one naming System.Exception itself — a specific exception
                        // type is a failure the handler genuinely anticipates, which is what Result is for.
                        { } caught && !SymbolEqualityComparer.Default.Equals(
                            semanticModel.GetTypeInfo(caught, ct).Type,
                            exceptionType))
                    {
                        continue;
                    }

                    if (!clause.Block.DescendantNodes(DescendIntoCatchBlock).OfType<InvocationExpressionSyntax>()
                            .Any(invocation => IsResultFailure(
                                semanticModel.GetSymbolInfo(invocation, ct).Symbol as
                                    IMethodSymbol,
                                resultType,
                                genericResultType)))
                    {
                        continue;
                    }

                    findings.Add(Finding.Create(Kind.CaughtExceptionAsResult, clause.GetLocation(), symbol.Name));
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
        || DerivesFrom(symbol, "Cntryl.Portia.Projector")
        || DerivesFrom(symbol, "Cntryl.Portia.Reactor")
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
            .Where(parameter => Matches(parameter.Type, forbidden));

    static bool Matches(ITypeSymbol type, string[] forbidden)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        for (var current = named; current is not null; current = current.BaseType)
        {
            if (Array.IndexOf(forbidden, MetadataName(current)) >= 0 ||
                current.AllInterfaces.Any(iface => Array.IndexOf(forbidden, MetadataName(iface)) >= 0))
            {
                return true;
            }
        }

        return false;
    }

    static string MetadataName(INamedTypeSymbol type)
    {
        type = type.OriginalDefinition;
        var names = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
            names.Push(current.MetadataName);
        var name = string.Join("+", names);
        return type.ContainingNamespace is { IsGlobalNamespace: false } space
            ? $"{space.ToDisplayString()}.{name}"
            : name;
    }

    static Location? Location(IParameterSymbol parameter) => parameter.Locations.FirstOrDefault();

    static string Display(IParameterSymbol parameter) => parameter.Type.Name;

    static DiagnosticDescriptor Descriptor(Kind kind) => kind switch
    {
        Kind.ProjectorEffect => ProjectorEffect,
        Kind.ServiceLocation => ServiceLocation,
        Kind.AggregateService => AggregateService,
        Kind.MultipleHandlers => MultipleHandlers,
        _ => CaughtExceptionAsResult
    };

    enum Kind { ProjectorEffect, ServiceLocation, AggregateService, MultipleHandlers, CaughtExceptionAsResult }

    sealed class Finding(Kind kind, SourceLocation location, object[] arguments) : IEquatable<Finding>
    {
        public Kind Kind { get; } = kind;
        public SourceLocation Location { get; } = location;
        public object[] Arguments { get; } = arguments;
        public static Finding Create(Kind kind, Location? location, params object[] arguments) =>
            new(kind, SourceLocation.From(location), arguments);
        public bool Equals(Finding? other) => other is not null && Kind == other.Kind && Location.Equals(other.Location)
                                             && Arguments.SequenceEqual(other.Arguments);
        public override bool Equals(object? obj) => Equals(obj as Finding);
        public override int GetHashCode() => Kind.GetHashCode();
    }

    readonly struct SourceLocation(string path, int start, int length, int startLine, int startCharacter,
        int endLine, int endCharacter) : IEquatable<SourceLocation>
    {
        readonly string _path = path;
        readonly int _start = start;
        readonly int _length = length;
        readonly int _startLine = startLine;
        readonly int _startCharacter = startCharacter;
        readonly int _endLine = endLine;
        readonly int _endCharacter = endCharacter;

        public static SourceLocation From(Location? location)
        {
            if (location is null || !location.IsInSource)
                return default;
            var lines = location.GetLineSpan().Span;
            return new SourceLocation(location.SourceTree?.FilePath ?? string.Empty, location.SourceSpan.Start,
                location.SourceSpan.Length, lines.Start.Line, lines.Start.Character, lines.End.Line,
                lines.End.Character);
        }
        public Microsoft.CodeAnalysis.Location ToLocation() =>
            string.IsNullOrEmpty(_path) && _start == 0 && _length == 0
                ? Microsoft.CodeAnalysis.Location.None
                : Microsoft.CodeAnalysis.Location.Create(_path, new TextSpan(_start, _length),
                    new LinePositionSpan(new LinePosition(_startLine, _startCharacter),
                        new LinePosition(_endLine, _endCharacter)));
        public bool Equals(SourceLocation other) => _path == other._path && _start == other._start &&
                                                    _length == other._length && _startLine == other._startLine &&
                                                    _startCharacter == other._startCharacter &&
                                                    _endLine == other._endLine &&
                                                    _endCharacter == other._endCharacter;
        public override bool Equals(object? obj) => obj is SourceLocation other && Equals(other);
        public override int GetHashCode() => (_path, _start, _length).GetHashCode();
    }
}

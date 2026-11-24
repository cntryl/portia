using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia;

/// <summary>
///     Reports application-design mistakes this architecture makes easy and nothing else catches.
///     These are warnings, not errors: each one restates a boundary Portia's own documentation
///     already asserts, and an application that means to cross one can suppress it deliberately.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ComponentPracticeAnalyzer : DiagnosticAnalyzer
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

    static readonly DiagnosticDescriptor ReactorPurity = new("PORTIA110", "Reactor dependency is outside its allow-list",
        "Reactor '{0}' takes '{1}'. This opt-in rule permits only IRequestBus and IProjectionCheckpointStore so reactions pivot through commands instead of reading or writing directly.",
        "Portia", DiagnosticSeverity.Warning, false);

    static readonly DiagnosticDescriptor ReactorBodyPurity = new("PORTIA111", "Reactor performs work beyond command dispatch",
        "Reactor '{0}' calls '{1}' in a handler. Read the trigger and send a command through IRequestBus; move every read, decision, and external effect into the command handler.",
        "Portia", DiagnosticSeverity.Warning, false);

    static readonly DiagnosticDescriptor CaughtExceptionAsResult = new("PORTIA104",
        "Unexpected failure converted to a Result",
        "'{0}' catches Exception and returns a failed Result. Result describes failures a handler anticipates; an unrecognized failure must stay an exception so transports can tell 'this request is invalid' from 'this call broke'.",
        "Portia", DiagnosticSeverity.Warning, true);

    static readonly DiagnosticDescriptor GuardEffect = new("PORTIA105",
        "Request guard takes effect-capable dependency",
        "Request guard '{0}' takes effect-capable dependency '{1}'; {2}. Guards are preflight checks that rerun on every retry and redelivery and must not change state.",
        "Portia", DiagnosticSeverity.Warning, true);

    static readonly DiagnosticDescriptor AuthorizerEffect = new("PORTIA106",
        "Request authorizer takes effect-capable dependency",
        "Request authorizer '{0}' takes effect-capable dependency '{1}'; {2}. Authorizers decide whether an actor may attempt a request and must not change state.",
        "Portia", DiagnosticSeverity.Warning, true);

    static readonly DiagnosticDescriptor SplitDependentReactions = new("PORTIA109",
        "Dependent reactions are split across reactors",
        "Reactor '{0}' sends '{1}' and reactor '{2}' sends '{3}' for the same '{4}' lifecycle. Consolidate these dependent reactions in one reactor so their checkpoints cannot put cancellation before creation.",
        "Portia", DiagnosticSeverity.Warning, false);

    // Anything that reaches outside the component's own unit of work.
    static readonly string[] EffectTypes =
    [
        "Cntryl.Portia.IRequestBus",
        "Cntryl.Portia.IRemoteRequestSender",
        "Cntryl.Portia.IRequestQueuePublisher",
        "Cntryl.Portia.INoticeRequestSender",
        "Cntryl.Portia.IRequestScheduler",
        "Cntryl.Portia.IAggregateWriter",
        "Cntryl.Portia.IAggregateExecutor",
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
        "Cntryl.Portia.IAggregateReader",
        "Cntryl.Portia.IEventStore",
        "Cntryl.Portia.IDomainEventReader",
        "Cntryl.Portia.IDomainEventWriter",
        "Cntryl.Portia.IProjectionStore",
        "Cntryl.Portia.IProjectionCheckpointStore"
    ];

    // Guards and authorizers read, often through an application's own HTTP policy service or read-model
    // database, so only Portia's own write and dispatch APIs count as effects here.
    static readonly string[] PreflightForbiddenTypes =
    [
        "Cntryl.Portia.IRequestBus",
        "Cntryl.Portia.IRemoteRequestSender",
        "Cntryl.Portia.IRequestQueuePublisher",
        "Cntryl.Portia.INoticeRequestSender",
        "Cntryl.Portia.IRequestScheduler",
        "Cntryl.Portia.IAggregateWriter",
        "Cntryl.Portia.IAggregateExecutor",
        "Cntryl.Portia.IEventStore",
        "Cntryl.Portia.IDomainEventWriter"
    ];

    // The projection contracts themselves commit progress. An application repository that also implements
    // one is matched only by the rule that owns it, since reading through it is legitimate.
    static readonly string[] ProjectionContracts =
    [
        "Cntryl.Portia.IProjectionStore",
        "Cntryl.Portia.IProjectionCheckpointStore"
    ];

    static readonly string[] DefaultReactorDependencies =
    [
        "Cntryl.Portia.IRequestBus",
        "Cntryl.Portia.IProjectionCheckpointStore"
    ];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        ProjectorEffect, ServiceLocation, AggregateService, ReactorPurity, CaughtExceptionAsResult, GuardEffect,
        AuthorizerEffect, SplitDependentReactions, ReactorBodyPurity
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        // Generated files are the application's code too: a partial component part often lives in one, and
        // skipping it would hide findings the generators always saw.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                               GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var reactions = new ConcurrentBag<ReactionHandlers>();
            var reported = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            start.RegisterSyntaxNodeAction(nodeContext =>
            {
                var invocation = (InvocationExpressionSyntax)nodeContext.Node;

                if (nodeContext.SemanticModel.GetSymbolInfo(invocation, nodeContext.CancellationToken).Symbol is not
                    IMethodSymbol target
                    || !IsReactionDispatch(target)
                    || invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is not { } declaration
                    || nodeContext.SemanticModel.GetDeclaredSymbol(declaration, nodeContext.CancellationToken) is not
                    { IsAbstract: false } reactor
                    || invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() is not
                    { } handlerSyntax
                    || nodeContext.SemanticModel.GetDeclaredSymbol(handlerSyntax, nodeContext.CancellationToken) is not
                        IMethodSymbol handler
                    || !IsReactorHandlerImplementation(reactor, handler)
                    || !ApplicationProject.IsReactor(reactor))
                    return;

                var command = invocation.ArgumentList.Arguments
                    .Select(argument => nodeContext.SemanticModel.GetTypeInfo(argument.Expression,
                        nodeContext.CancellationToken).Type)
                    .OfType<INamedTypeSymbol>()
                    .FirstOrDefault(ApplicationProject.IsRequest);

                if (command is null)
                    return;

                var current = new ReactionHandlers(reactor, [command]);
                reactions.Add(current);

                foreach (var other in reactions)
                {
                    if (SymbolEqualityComparer.Default.Equals(other.Reactor, reactor))
                        continue;

                    var lifecycle = LifecyclePair(command, other.Commands[0]);

                    if (lifecycle is null)
                        continue;

                    var left = reactor.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var right = other.Reactor.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var key = StringComparer.Ordinal.Compare(left, right) < 0
                        ? left + "|" + right
                        : right + "|" + left;

                    if (reported.TryAdd(key, 0))
                        nodeContext.ReportDiagnostic(Diagnostic.Create(SplitDependentReactions,
                            reactor.Locations.FirstOrDefault(), reactor.Name,
                            command.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                            other.Reactor.Name,
                            other.Commands[0].ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                            lifecycle));
                }
            }, SyntaxKind.InvocationExpression);
        });
        context.RegisterSyntaxNodeAction(static syntaxContext =>
        {
            // Every rule concerns a type that derives from or implements something. A partial part without a
            // base list may still belong to one, so it is analyzed too.
            var node = (TypeDeclarationSyntax)syntaxContext.Node;

            if (node.BaseList is null && !node.Modifiers.Any(SyntaxKind.PartialKeyword))
                return;

            var allowedReactorDependencies = ReactorDependencies(
                syntaxContext.Options.AnalyzerConfigOptionsProvider, node.SyntaxTree);

            foreach (var finding in Analyze(node, syntaxContext.SemanticModel, allowedReactorDependencies,
                         syntaxContext.CancellationToken))
            {
                syntaxContext.ReportDiagnostic(Diagnostic.Create(Descriptor(finding.Kind), finding.Location,
                    finding.Arguments));
            }
        }, SyntaxKind.ClassDeclaration, SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
    }

    static Finding[] Analyze(
        TypeDeclarationSyntax node,
        SemanticModel semanticModel,
        string[] allowedReactorDependencies,
        CancellationToken ct
    )
    {
        if (semanticModel.GetDeclaredSymbol(node, ct) is not { IsAbstract: false } symbol)
            return [];

        var findings = new List<Finding>();
        var parameters = DeclaredParameters(symbol, node);
        ReportProjectorEffects(findings, symbol, parameters);
        ReportReactorPurity(findings, symbol, parameters, allowedReactorDependencies);
        ReportReactorBodyPurity(findings, symbol, node, semanticModel, ct);
        ReportAggregateServices(findings, symbol, parameters);
        ReportPreflightEffects(findings, symbol, parameters);
        ReportServiceLocation(findings, symbol, node, semanticModel, parameters);
        ReportCaughtExceptionAsResult(findings, symbol, node, semanticModel, ct);
        return [.. findings];
    }

    static void ReportReactorPurity(
        List<Finding> findings,
        INamedTypeSymbol symbol,
        IParameterSymbol[] parameters,
        string[] allowedReactorDependencies
    )
    {
        if (!ApplicationProject.IsReactor(symbol))
            return;

        foreach (var parameter in parameters.Where(parameter =>
                     !Matches(parameter.Type, allowedReactorDependencies)))
            findings.Add(Finding.Create(Kind.ReactorPurity, Location(parameter), symbol.Name,
                Display(parameter)));
    }

    static void ReportReactorBodyPurity(
        List<Finding> findings,
        INamedTypeSymbol symbol,
        TypeDeclarationSyntax node,
        SemanticModel semanticModel,
        CancellationToken ct
    )
    {
        if (!ApplicationProject.IsReactor(symbol))
            return;

        var handlerMethods = symbol.AllInterfaces
            .Where(iface => SymbolNames.Is(iface, "Cntryl.Portia.IReactorHandler`1")
                            || SymbolNames.Is(iface, "Cntryl.Portia.IBatchReactorHandler`1"))
            .SelectMany(iface => iface.GetMembers("HandleAsync").OfType<IMethodSymbol>())
            .Select(symbol.FindImplementationForInterfaceMember)
            .OfType<IMethodSymbol>()
            .Where(method => SymbolEqualityComparer.Default.Equals(method.ContainingType, symbol));

        foreach (var implementation in handlerMethods)
            foreach (var reference in implementation.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree != node.SyntaxTree
                    || reference.GetSyntax(ct) is not MethodDeclarationSyntax method
                    || !method.Ancestors().Contains(node))
                    continue;

                foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (semanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol target
                        || IsReactionDispatch(target)
                        || target.IsImplicitlyDeclared)
                        continue;

                    findings.Add(Finding.Create(Kind.ReactorBodyPurity, invocation.GetLocation(), symbol.Name,
                        target.Name));
                }
            }
    }

    static string? LifecyclePair(INamedTypeSymbol left, INamedTypeSymbol right)
    {
        if (!SymbolEqualityComparer.Default.Equals(left.ContainingNamespace, right.ContainingNamespace))
            return null;

        var leftAction = ActionFor(left.Name, out var leftResource);
        var rightAction = ActionFor(right.Name, out var rightResource);

        return leftAction.HasValue && rightAction.HasValue
                                  && leftAction != rightAction
                                  && string.Equals(leftResource, rightResource, StringComparison.Ordinal)
            ? rightResource
            : null;
    }

    static bool IsReactionDispatch(IMethodSymbol method)
    {
        method = method.ReducedFrom ?? method;
        return method.Name == "SendReactionAsync"
               && SymbolNames.Is(method.ContainingType, "Cntryl.Portia.RequestBusExtensions");
    }

    static bool IsReactorHandlerImplementation(INamedTypeSymbol reactor, IMethodSymbol method) =>
        reactor.AllInterfaces
            .Where(iface => SymbolNames.Is(iface, "Cntryl.Portia.IReactorHandler`1")
                            || SymbolNames.Is(iface, "Cntryl.Portia.IBatchReactorHandler`1"))
            .SelectMany(iface => iface.GetMembers("HandleAsync").OfType<IMethodSymbol>())
            .Select(reactor.FindImplementationForInterfaceMember)
            .Any(implementation => SymbolEqualityComparer.Default.Equals(implementation, method));

    static string[] ReactorDependencies(
        AnalyzerConfigOptionsProvider options,
        SyntaxTree tree
    )
    {
        if (!options.GetOptions(tree).TryGetValue("portia_reactor_allowed_dependencies", out var configured)
            || string.IsNullOrWhiteSpace(configured))
            return DefaultReactorDependencies;

        var configuredTypes = configured.Split(',')
            .Select(type => type.Trim())
            .Where(type => type.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return configuredTypes.Length == 0 ? DefaultReactorDependencies : configuredTypes;
    }

    static bool? ActionFor(string name, out string resource)
    {
        foreach (var prefix in new[] { "Create", "Cancel" })
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length)
                continue;

            resource = name.Substring(prefix.Length);
            return prefix == "Create";
        }

        resource = "";
        return null;
    }
    static void ReportProjectorEffects(List<Finding> findings, INamedTypeSymbol symbol, IParameterSymbol[] parameters)
    {
        if (!ApplicationProject.IsProjector(symbol))
            return;
        // The projector's own projection store shares its commit, whatever else that repository derives from.
        foreach (var parameter in Parameters(parameters, EffectTypes)
                     .Where(parameter => !Matches(parameter.Type, ProjectionContracts)))
        {
            findings.Add(Finding.Create(Kind.ProjectorEffect, Location(parameter), symbol.Name, Display(parameter)));
        }
    }

    static void ReportPreflightEffects(List<Finding> findings, INamedTypeSymbol symbol, IParameterSymbol[] parameters)
    {
        var guard = symbol.AllInterfaces.Any(iface => PortiaComponentRoles.Is(iface, PortiaComponentRoles.Guard));
        var authorizer =
            symbol.AllInterfaces.Any(iface => PortiaComponentRoles.Is(iface, PortiaComponentRoles.Authorizer));

        if (!guard && !authorizer)
            return;

        foreach (var parameter in Parameters(parameters, PreflightForbiddenTypes).Concat(parameters
                     .Where(parameter => parameter.Type is INamedTypeSymbol named
                                         && IsAny(named, ProjectionContracts))))
        {
            var remedy = Matches(parameter.Type, ["Cntryl.Portia.IAggregateWriter", "Cntryl.Portia.IAggregateExecutor"])
                ? "inject IAggregateReader to hydrate aggregates"
                : Matches(parameter.Type, ["Cntryl.Portia.IEventStore"])
                    ? "inject IDomainEventReader to read events"
                    : Matches(parameter.Type, ProjectionContracts)
                        ? "inject an application-owned query interface that exposes only the read this preflight needs"
                        : "move the effect into the request handler";

            if (guard)
                findings.Add(Finding.Create(Kind.GuardEffect, Location(parameter), symbol.Name, Display(parameter),
                    remedy));

            if (authorizer)
                findings.Add(Finding.Create(Kind.AuthorizerEffect, Location(parameter), symbol.Name, Display(parameter),
                    remedy));
        }
    }

    static void ReportServiceLocation(
        List<Finding> findings,
        INamedTypeSymbol symbol,
        TypeDeclarationSyntax node,
        SemanticModel semanticModel,
        IParameterSymbol[] parameters
    )
    {
        if (!IsPortiaComponent(symbol))
            return;
        // PORTIA102 already reports every locator an aggregate takes.
        var aggregate = DerivesFrom(symbol, "Cntryl.Portia.Aggregate");

        foreach (var parameter in aggregate ? [] : Parameters(parameters, LocatorTypes))
        {
            findings.Add(Finding.Create(Kind.ServiceLocation, Location(parameter), symbol.Name, Display(parameter)));
        }

        // Only ActivatorUtilities' own method names are bound; every other call in the component is skipped.

        foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>()
                     .Where(candidate => IsActivatorUtilitiesName(candidate)
                                         && candidate.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()
                                         == node))
        {
            if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
                !SymbolNames.Is(method.ContainingType, "Microsoft.Extensions.DependencyInjection.ActivatorUtilities"))
            {
                continue;
            }

            findings.Add(Finding.Create(Kind.ServiceLocation, invocation.GetLocation(), symbol.Name,
                $"ActivatorUtilities.{method.Name}"));
        }
    }

    static bool IsActivatorUtilitiesName(InvocationExpressionSyntax invocation) =>
        (invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name,
            SimpleNameSyntax simple => simple,
            _ => null
        })?.Identifier.ValueText is "CreateInstance" or "GetServiceOrCreateInstance" or "CreateFactory";

    static void ReportAggregateServices(List<Finding> findings, INamedTypeSymbol symbol, IParameterSymbol[] parameters)
    {
        if (!DerivesFrom(symbol, "Cntryl.Portia.Aggregate"))
            return;

        foreach (var parameter in Parameters(parameters, AggregateForbiddenTypes))
        {
            findings.Add(Finding.Create(Kind.AggregateService, Location(parameter), symbol.Name, Display(parameter)));
        }
    }

    static void ReportCaughtExceptionAsResult(
        List<Finding> findings,
        INamedTypeSymbol symbol,
        TypeDeclarationSyntax node,
        SemanticModel semanticModel,
        CancellationToken ct
    )
    {
        // Type lookups search every referenced assembly, so they wait until this declaration is known to be a
        // handler that catches something.
        if (!node.DescendantNodes().OfType<CatchClauseSyntax>().Any()
            || !symbol.AllInterfaces.Any(iface => PortiaComponentRoles.Is(iface, PortiaComponentRoles.Handler)))
        {
            return;
        }

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
                    // A filter explicitly narrows which System.Exception values this catch accepts. Treat it
                    // like a specific catch: the handler has named the failure it anticipates.
                    if (clause.Filter is not null)
                        continue;

                    // Syntax first: only a catch that returns a call named Failure can be this shape, so every
                    // other catch is skipped before its body is bound.
                    if (!clause.Block.Statements.OfType<ReturnStatementSyntax>().Any(statement =>
                            statement.Expression?.DescendantNodesAndSelf(DescendIntoHandlerBody)
                                .OfType<InvocationExpressionSyntax>().Any(IsNamedFailure) == true))
                    {
                        continue;
                    }

                    if (clause.Declaration?.Type is
                        // A bare catch, or one naming System.Exception itself — a specific exception
                        // type is a failure the handler genuinely anticipates, which is what Result is for.
                        { } caught && !SymbolEqualityComparer.Default.Equals(
                            semanticModel.GetTypeInfo(caught, ct).Type,
                            exceptionType))
                    {
                        continue;
                    }

                    // Report only the shape named by the diagnostic: a failed Result returned directly from
                    // the catch. Merely constructing a failure value, or returning one only from a narrowed
                    // branch before rethrowing everything else, does not convert an unexpected exception.
                    if (!clause.Block.Statements.OfType<ReturnStatementSyntax>().Any(statement =>
                            statement.Expression is { } expression
                            && !expression.DescendantNodesAndSelf().OfType<ThrowExpressionSyntax>().Any()
                            && expression.DescendantNodesAndSelf(DescendIntoHandlerBody)
                                .OfType<InvocationExpressionSyntax>()
                                .Any(invocation => IsResultFailure(
                                    semanticModel.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol,
                                    resultType,
                                    genericResultType))))
                    {
                        continue;
                    }

                    findings.Add(Finding.Create(Kind.CaughtExceptionAsResult, clause.GetLocation(), symbol.Name));
                }
            }
        }

        static bool IsNamedFailure(InvocationExpressionSyntax invocation) =>
            (invocation.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name,
                SimpleNameSyntax simple => simple,
                _ => null
            })?.Identifier.ValueText == "Failure";

        static bool DescendIntoHandlerBody(SyntaxNode current)
        {
            return current is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        }

        static bool IsResultFailure(
            IMethodSymbol? method,
            INamedTypeSymbol result,
            INamedTypeSymbol genericResult
        )
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
        || ApplicationProject.IsProjector(symbol)
        || DerivesFrom(symbol, "Cntryl.Portia.Reactor")
        || DerivesFrom(symbol, "Cntryl.Portia.Aggregate");

    static bool DerivesFrom(INamedTypeSymbol symbol, string baseTypeName)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolNames.Is(current, baseTypeName))
                return true;
        }

        return false;
    }

    // Constructors belong to the type, not to the declaration being visited, so a partial type reports each
    // constructor parameter from the declaration that holds it: analyzing one file finds everything located in it,
    // and the whole compilation still reports each parameter once.
    static IParameterSymbol[] DeclaredParameters(INamedTypeSymbol symbol, TypeDeclarationSyntax node) =>
    [
        .. symbol.InstanceConstructors
            .Where(constructor => !constructor.IsImplicitlyDeclared)
            .SelectMany(constructor => constructor.Parameters)
            .Where(parameter => parameter.Locations.FirstOrDefault() is { } location
                                && location.SourceTree == node.SyntaxTree
                                && node.Span.Contains(location.SourceSpan))
    ];

    static IEnumerable<IParameterSymbol> Parameters(IParameterSymbol[] parameters, string[] forbidden) =>
        parameters.Where(parameter => Matches(parameter.Type, forbidden));

    static bool Matches(ITypeSymbol type, string[] forbidden)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        for (var current = named; current is not null; current = current.BaseType)
        {
            if (IsAny(current, forbidden) || current.AllInterfaces.Any(iface => IsAny(iface, forbidden)))
                return true;
        }

        return false;
    }

    static bool IsAny(INamedTypeSymbol type, string[] names)
    {
        foreach (var name in names)
        {
            if (SymbolNames.Is(type, name))
                return true;
        }

        return false;
    }

    static Location? Location(IParameterSymbol parameter) => parameter.Locations.FirstOrDefault();

    static string Display(IParameterSymbol parameter) => parameter.Type.Name;

    static DiagnosticDescriptor Descriptor(Kind kind) => kind switch
    {
        Kind.ProjectorEffect => ProjectorEffect,
        Kind.ServiceLocation => ServiceLocation,
        Kind.AggregateService => AggregateService,
        Kind.ReactorPurity => ReactorPurity,
        Kind.CaughtExceptionAsResult => CaughtExceptionAsResult,
        Kind.GuardEffect => GuardEffect,
        Kind.AuthorizerEffect => AuthorizerEffect,
        Kind.ReactorBodyPurity => ReactorBodyPurity,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    enum Kind
    {
        ProjectorEffect,
        ServiceLocation,
        AggregateService,
        ReactorPurity,
        CaughtExceptionAsResult,
        GuardEffect,
        AuthorizerEffect,
        ReactorBodyPurity
    }

    sealed record ReactionHandlers(
        INamedTypeSymbol Reactor,
        INamedTypeSymbol[] Commands);

    sealed class Finding(Kind kind, Location location, object[] arguments)
    {
        public Kind Kind { get; } = kind;
        public Location Location { get; } = location;
        public object[] Arguments { get; } = arguments;

        public static Finding Create(Kind kind, Location? location, params object[] arguments) =>
            new(kind, location ?? Microsoft.CodeAnalysis.Location.None, arguments);
    }
}

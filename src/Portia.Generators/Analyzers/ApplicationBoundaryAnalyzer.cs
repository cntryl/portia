using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Cntryl.Portia;

/// <summary>Checks application dependencies, registration ownership, and request execution boundaries.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ApplicationBoundaryAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Dependency = new("PORTIA206",
        "Application dependency crosses its project boundary",
        "Project '{0}' references '{1}'; Common owns contracts, Core depends on Common, application *.Abstractions projects, and Portia APIs, and Api/Worker/Mcp configure concrete infrastructure",
        "PortiaStructure", DiagnosticSeverity.Warning, true, customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    static readonly DiagnosticDescriptor Registration = new("PORTIA207",
        "Component registration is outside its feature registration file",
        "Register {1} feature component(s) in Features/{0}/{0}PortiaExtensions.cs", "PortiaStructure",
        DiagnosticSeverity.Warning, true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    static readonly DiagnosticDescriptor CommandStub = new("PORTIA211", "Command handler is a stub",
        "Command handler '{0}' only throws NotImplementedException; implement the business operation or remove its surface",
        "PortiaStructure", DiagnosticSeverity.Warning, true);

    static readonly DiagnosticDescriptor Hydration = new("PORTIA212", "Query or reactor hydrates an aggregate",
        "'{0}' calls '{1}'. Use a read model in queries and dispatch commands from reactors. Command handlers can hydrate other aggregates for advisory validation. Completion still requires authoritative outcomes from the owning aggregates.",
        "PortiaStructure", DiagnosticSeverity.Warning, true, customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    static readonly DiagnosticDescriptor InternalRequestRegistration = new("PORTIA214",
        "System request handler is not registered",
        "System request handler '{0}' is not registered. Register it in the owning feature with AddRequestHandler<{0}> so every host that composes that feature receives the internal command.",
        "PortiaStructure", DiagnosticSeverity.Warning, true, customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [Dependency, Registration, CommandStub, Hydration, InternalRequestRegistration];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (ApplicationProject.From(start.Options.AnalyzerConfigOptionsProvider) is not { IsTest: false } project)
                return;

            var registrations = new ConcurrentBag<(Location Location, string Feature)>();
            var registeredHandlers = new ConcurrentBag<INamedTypeSymbol>();
            var hydrationCalls = project.Role == "Core"
                ? new HydrationCallGraph(start.Compilation, start.CancellationToken)
                : null;
            start.RegisterCompilationEndAction(end =>
            {
                AnalyzeDependencies(end, project);
                ReportRegistrations(end, registrations);
                ReportInternalRequestRegistrations(end, registeredHandlers);

                hydrationCalls?.Report(end);
            });
            start.RegisterOperationAction(operation =>
                    AnalyzeInvocation(operation, project, registrations, registeredHandlers, hydrationCalls),
                OperationKind.Invocation);

            if (project.Role is "Common" or "Core")
            {
                start.RegisterOperationAction(operation => AnalyzeConnectionConstruction(operation, project),
                    OperationKind.ObjectCreation);
            }


            start.RegisterSyntaxNodeAction(AnalyzeStub, SyntaxKind.MethodDeclaration);
        });
    }

    static void AnalyzeDependencies(CompilationAnalysisContext context, ApplicationProject project)
    {
        if (project.Role is not ("Common" or "Core" or "Api" or "Worker" or "Mcp" or "Host" or "Shared"))
            return;

        var references = context.Compilation.ReferencedAssemblyNames;
        var assemblyName = context.Compilation.AssemblyName ?? project.Name;
        var suffix = "." + project.Role;
        var applicationName = assemblyName.EndsWith(suffix, StringComparison.Ordinal)
            ? assemblyName.Substring(0, assemblyName.Length - suffix.Length)
            : project.ApplicationName;
        var cassieApi = references.Any(reference => reference.Name is "Portia.Cassie" or "Cntryl.Portia.Cassie");
        var siblingRoles = project.Role switch
        {
            "Api" => new[] { "Worker", "Mcp" },
            "Worker" => new[] { "Api", "Mcp" },
            "Mcp" => new[] { "Api", "Worker" },
            _ => []
        };

        foreach (var reference in references)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var name = reference.Name;
            var applicationDependency = project.Role is "Common" or "Core"
                                        && name.StartsWith(applicationName + ".", StringComparison.Ordinal)
                                        && !(project.Role == "Core" && (name == applicationName + ".Common"
                                                                        || name.EndsWith(".Abstractions",
                                                                            StringComparison.Ordinal)));
            var frameworkName = name.StartsWith("Cntryl.Portia.", StringComparison.Ordinal) ? name.Substring(7) : name;
            var commonBehavior =
                project.Role == "Common" && frameworkName.StartsWith("Portia.", StringComparison.Ordinal)
                                         && frameworkName is not ("Portia.Abstractions" or "Portia.Storage.Abstractions"
                                             or "Portia.Communications.Abstractions" or "Portia.DataProtection.Abstractions"
                                             or "Portia.Secrets.Abstractions");
            var infrastructure = project.Role is "Common" or "Core"
                                 && (frameworkName is "Portia.Fitz" or "Portia.AspNetCore" or "Portia.Mcp.AspNetCore"
                                     or "Portia.Admin.Mcp.AspNetCore" or "Portia.Storage.Aws" or "Portia.Storage.Azure"
                                     or "Portia.Storage.Gcp"
                                     or "Portia.Communications.Aws" or "Portia.Communications.Azure"
                                     or "Portia.Communications.Twilio" or "Portia.DataProtection"
                                     or "Portia.DataProtection.Aws" or "Portia.Secrets.Aws"
                                 || name is "Cntryl.Fitz.Core" or "Cntryl.Fitz.Extensions"
                                 || name == "Cntryl.Cassie" && !(project.Role == "Core" && cassieApi));
            var siblingHost = siblingRoles.Any(role => name == applicationName + "." + role);

            if (applicationDependency || commonBehavior || infrastructure || siblingHost)
                context.ReportDiagnostic(Diagnostic.Create(Dependency, Location.None, project.Name, name));
        }
    }

    static void AnalyzeInvocation(
        OperationAnalysisContext context,
        ApplicationProject project,
        ConcurrentBag<(Location Location, string Feature)> registrations,
        ConcurrentBag<INamedTypeSymbol> registeredHandlers,
        HydrationCallGraph? hydrationCalls
    )
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var hydrates = Hydrates(method);

        if (hydrationCalls is not null && context.ContainingSymbol is IMethodSymbol caller)
        {
            hydrationCalls.Add(caller.OriginalDefinition, method.OriginalDefinition, invocation.Syntax, hydrates);
        }

        if (project.Role == "Core" && IsComponentRegistration(method))
        {
            if (method.Name == "AddRequestHandler" &&
                method.TypeArguments.FirstOrDefault() is INamedTypeSymbol registeredHandler)
                registeredHandlers.Add(registeredHandler);

            var path = project.RelativePath(invocation.Syntax.SyntaxTree);
            var feature = FeatureOf(method.TypeArguments.FirstOrDefault())
                          ?? (path.Length >= 2 && path[0] == "Features" ? path[1] : "Feature");

            if (path.Length != 3 || path[0] != "Features" || path[1] != feature ||
                path[2] != feature + "PortiaExtensions.cs")
                registrations.Add((invocation.Syntax.GetLocation(), feature));
        }

        if (project.Role is "Common" or "Core" && method.Name == "AddCassie"
                                               && SymbolNames.Is(method.ContainingType,
                                                   "Cntryl.Portia.CassiePortiaBuilderExtensions"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Dependency, invocation.Syntax.GetLocation(), project.Name,
                "CassiePortiaBuilderExtensions.AddCassie"));
        }


        if (!hydrates || context.ContainingSymbol.ContainingType is not { } component)
            return;

        var query = ApplicationProject.Requests(component)
            .Any(request => ApplicationProject.IsContractIn(request.TypeArguments[0], "Queries"));

        if (query || ApplicationProject.IsReactor(component))
            context.ReportDiagnostic(Diagnostic.Create(Hydration, invocation.Syntax.GetLocation(), component.Name,
                method.Name));
    }

    sealed class HydrationCallGraph
    {
        readonly IAssemblySymbol _assembly;
        readonly Dictionary<ISymbol, string> _queryEntries = [with(SymbolEqualityComparer.Default)];
        readonly Dictionary<ISymbol, HashSet<ISymbol>> _callersByTarget = [with(SymbolEqualityComparer.Default)];
        readonly HashSet<ISymbol> _reachesHydration = [with(SymbolEqualityComparer.Default)];
        readonly List<(string Handler, IMethodSymbol Target, Location Site)> _queryCalls = [];

        public HydrationCallGraph(Compilation compilation, CancellationToken ct)
        {
            _assembly = compilation.Assembly;

            foreach (var type in compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type, ct).OfType<INamedTypeSymbol>())
            {
                ct.ThrowIfCancellationRequested();

                foreach (var contract in ApplicationProject.Requests(type).Where(request =>
                             ApplicationProject.IsContractIn(request.TypeArguments[0], "Queries")))
                    foreach (var member in contract.GetMembers("HandleAsync"))
                    {
                        if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol handler
                            && (!_queryEntries.TryGetValue(handler.OriginalDefinition, out var alias)
                                || string.CompareOrdinal(type.Name, alias) < 0))
                            _queryEntries[handler.OriginalDefinition] = type.Name;
                    }
            }
        }

        public void Add(IMethodSymbol caller, IMethodSymbol target, SyntaxNode syntax, bool hydrates)
        {
            if (_queryEntries.Count == 0 || (!hydrates && !SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, _assembly)))
                return;

            lock (_callersByTarget)
            {
                if (hydrates)
                {
                    _reachesHydration.Add(caller);
                    return;
                }

                if (!_callersByTarget.TryGetValue(target, out var callers))
                    _callersByTarget.Add(target, callers = [with(SymbolEqualityComparer.Default)]);

                callers.Add(caller);

                if (_queryEntries.TryGetValue(caller, out var handler))
                    _queryCalls.Add((handler, target, syntax.GetLocation()));
            }
        }

        public void Report(CompilationAnalysisContext context)
        {
            if (_queryCalls.Count == 0 || _reachesHydration.Count == 0)
                return;

            var pending = new Queue<ISymbol>(_reachesHydration);

            while (pending.Count > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (!_callersByTarget.TryGetValue(pending.Dequeue(), out var callers))
                    continue;

                foreach (var caller in callers)
                    if (_reachesHydration.Add(caller))
                        pending.Enqueue(caller);
            }

            foreach (var (handler, target, site) in _queryCalls)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (_reachesHydration.Contains(target))
                    context.ReportDiagnostic(Diagnostic.Create(Hydration, site, handler, target.Name));
            }
        }
    }

    static void ReportInternalRequestRegistrations(
        CompilationAnalysisContext context,
        ConcurrentBag<INamedTypeSymbol> registeredHandlers
    )
    {
        var registered = new HashSet<ISymbol>(registeredHandlers, SymbolEqualityComparer.Default);

        foreach (var handler in context.Compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type)
                     .OfType<INamedTypeSymbol>()
                     .Where(type => !type.IsAbstract && type.DeclaringSyntaxReferences.Length != 0
                                    && ApplicationProject.Requests(type).Any(request =>
                                        request.TypeArguments[0] is INamedTypeSymbol requestType
                                        && requestType.AllInterfaces.Any(iface =>
                                            SymbolNames.Is(iface, "Cntryl.Portia.ISystemRequest")))))
        {
            if (!registered.Contains(handler))
                context.ReportDiagnostic(Diagnostic.Create(InternalRequestRegistration,
                    handler.Locations.FirstOrDefault(), handler.Name));
        }
    }

    static void ReportRegistrations(
        CompilationAnalysisContext context,
        ConcurrentBag<(Location Location, string Feature)> registrations
    )
    {
        foreach (var group in registrations.GroupBy(item => (item.Location.SourceTree, item.Feature)))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var candidates = group.OrderBy(item => item.Location.SourceSpan.Start)
                .Select(item => Diagnostic.Create(Registration, item.Location, item.Feature, 1)).ToImmutableArray();
            var effective = CompilationWithAnalyzers.GetEffectiveDiagnostics(candidates, context.Compilation)
                .Where(diagnostic => !diagnostic.IsSuppressed).ToArray();

            if (effective.Length == 0)
                continue;

            context.ReportDiagnostic(Diagnostic.Create(Registration, effective[0].Location,
                effective.Skip(1).Select(diagnostic => diagnostic.Location), properties: null, group.Key.Feature,
                effective.Length));
        }
    }

    static bool IsComponentRegistration(IMethodSymbol method) =>
        SymbolNames.Is(method.ContainingType, "Cntryl.Portia.PortiaBuilder")
        && method.Name is "AddRequestHandler" or "AddProjector" or "AddReactor" or "AddRequestAuthorizer"
            or "AddRequestGuard" or "AddRequestPipelineBehavior"
        || SymbolNames.Is(method.ContainingType, "Cntryl.Portia.CassiePortiaBuilderExtensions")
        && method.Name == "AddCassieProjection";

    static string? FeatureOf(ITypeSymbol? symbol)
    {
        for (var space = symbol?.ContainingNamespace;
             space is { IsGlobalNamespace: false };
             space = space.ContainingNamespace)
        {
            if (space.ContainingNamespace.Name == "Features")
                return space.Name;
        }

        return null;
    }

    static bool Hydrates(IMethodSymbol method)
    {
        var contract = method.Name switch
        {
            "HydrateAsync" => "Cntryl.Portia.IAggregateReader",
            "ExecuteAsync" => "Cntryl.Portia.IAggregateExecutor",
            _ => null
        };
        return contract is not null && (SymbolNames.Is(method.ContainingType, contract)
                                        || method.ContainingType.AllInterfaces.Any(iface =>
                                            SymbolNames.Is(iface, contract)));
    }

    static void AnalyzeConnectionConstruction(OperationAnalysisContext context, ApplicationProject project)
    {
        var creation = (IObjectCreationOperation)context.Operation;

        if (creation.Type is INamedTypeSymbol type && (SymbolNames.Is(type, "Cntryl.Portia.CassieProjectionDatabase")
                                                       || SymbolNames.Is(type,
                                                           "Cntryl.Cassie.Connection.CassieDataSource")))
        {
            context.ReportDiagnostic(Diagnostic.Create(Dependency, creation.Syntax.GetLocation(), project.Name,
                type.Name));
        }
    }

    static void AnalyzeStub(SyntaxNodeAnalysisContext context)
    {
        var declaration = (MethodDeclarationSyntax)context.Node;
        var expression = declaration.ExpressionBody?.Expression is ThrowExpressionSyntax thrown
            ? thrown.Expression
            : declaration.Body is { Statements.Count: 1 } body && body.Statements[0] is ThrowStatementSyntax statement
                ? statement.Expression
                : null;

        if (expression is null || context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type is not
                                   INamedTypeSymbol exception
                               || !SymbolNames.Is(exception, "System.NotImplementedException")
                               || context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not
                               { } method)
        {
            return;
        }


        var handler = method.ContainingType;
        var commandImplementation = ApplicationProject.Requests(handler).Any(request =>
            ApplicationProject.IsContractIn(request.TypeArguments[0], "Commands")
            && request.GetMembers("HandleAsync").Any(member => SymbolEqualityComparer.Default.Equals(
                handler.FindImplementationForInterfaceMember(member), method)));

        if (commandImplementation)
            context.ReportDiagnostic(Diagnostic.Create(CommandStub, declaration.Identifier.GetLocation(),
                handler.Name));
    }
}

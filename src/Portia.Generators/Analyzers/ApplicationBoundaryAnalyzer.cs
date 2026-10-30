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
    static readonly DiagnosticDescriptor Dependency = new("PORTIA206", "Application dependency crosses its project boundary",
        "Project '{0}' references '{1}'; Common owns contracts, Core depends on Common and Portia APIs, and Api/Worker configure concrete infrastructure",
        "Portia", DiagnosticSeverity.Warning, true, customTags: [WellKnownDiagnosticTags.CompilationEnd]);
    static readonly DiagnosticDescriptor Registration = new("PORTIA207", "Component registration is outside its feature registration file",
        "Register feature components in Features/{0}/{0}PortiaExtensions.cs", "Portia", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor CommandStub = new("PORTIA211", "Command handler is a stub",
        "Command handler '{0}' only throws NotImplementedException; implement the business operation or remove its surface",
        "Portia", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor Hydration = new("PORTIA212", "Query or reactor hydrates an aggregate",
        "'{0}' calls '{1}'; queries read through repositories and reactors send commands from committed facts. Keep aggregate hydration in command handling.",
        "Portia", DiagnosticSeverity.Warning, true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [Dependency, Registration, CommandStub, Hydration];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (ApplicationProject.From(start.Options.AnalyzerConfigOptionsProvider) is not { IsTest: false } project)
                return;
            start.RegisterCompilationEndAction(end => AnalyzeDependencies(end, project));
            start.RegisterOperationAction(operation => AnalyzeInvocation(operation, project), OperationKind.Invocation);
            if (project.Role is "Common" or "Core")
            {
                start.RegisterOperationAction(operation => AnalyzeConnectionConstruction(operation, project), OperationKind.ObjectCreation);
            }
            start.RegisterSyntaxNodeAction(AnalyzeStub, SyntaxKind.MethodDeclaration);
        });
    }

    static void AnalyzeDependencies(CompilationAnalysisContext context, ApplicationProject project)
    {
        if (project.Role is not ("Common" or "Core"))
            return;
        var references = context.Compilation.ReferencedAssemblyNames;
        var assemblyName = context.Compilation.AssemblyName ?? project.Name;
        var suffix = "." + project.Role;
        var applicationName = assemblyName.EndsWith(suffix, StringComparison.Ordinal)
            ? assemblyName.Substring(0, assemblyName.Length - suffix.Length) : project.ApplicationName;
        var cassieApi = references.Any(reference => reference.Name is "Portia.Cassie" or "Cntryl.Portia.Cassie");
        foreach (var reference in references)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var name = reference.Name;
            var applicationDependency = name.StartsWith(applicationName + ".", StringComparison.Ordinal)
                && !(project.Role == "Core" && name == applicationName + ".Common");
            var frameworkName = name.StartsWith("Cntryl.Portia.", StringComparison.Ordinal) ? name.Substring(7) : name;
            var commonBehavior = project.Role == "Common" && frameworkName.StartsWith("Portia.", StringComparison.Ordinal)
                && frameworkName is not ("Portia.Abstractions" or "Portia.Storage.Abstractions");
            var infrastructure = frameworkName is "Portia.Fitz" or "Portia.AspNetCore" or "Portia.Mcp.AspNetCore"
                or "Portia.Admin.Mcp.AspNetCore" or "Portia.Storage.Aws"
                || name is "Cntryl.Fitz.Core" or "Cntryl.Fitz.Extensions"
                || name == "Cntryl.Cassie" && !(project.Role == "Core" && cassieApi);
            if (applicationDependency || commonBehavior || infrastructure)
                context.ReportDiagnostic(Diagnostic.Create(Dependency, Location.None, project.Name, name));
        }
    }

    static void AnalyzeInvocation(OperationAnalysisContext context, ApplicationProject project)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        if (project.Role == "Core" && IsComponentRegistration(method))
        {
            var path = project.RelativePath(invocation.Syntax.SyntaxTree);
            var feature = FeatureOf(method.TypeArguments.FirstOrDefault())
                ?? (path.Length >= 2 && path[0] == "Features" ? path[1] : "Feature");
            if (path.Length != 3 || path[0] != "Features" || path[1] != feature || path[2] != feature + "PortiaExtensions.cs")
                context.ReportDiagnostic(Diagnostic.Create(Registration, invocation.Syntax.GetLocation(), feature));
        }

        if (project.Role is "Common" or "Core" && method.Name == "AddCassie"
            && SymbolNames.Is(method.ContainingType, "Cntryl.Portia.CassiePortiaBuilderExtensions"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Dependency, invocation.Syntax.GetLocation(), project.Name,
                "CassiePortiaBuilderExtensions.AddCassie"));
        }

        if (!Hydrates(method) || context.ContainingSymbol.ContainingType is not { } component)
            return;
        var query = ApplicationProject.Requests(component).Any(request => ApplicationProject.IsContractIn(request.TypeArguments[0], "Queries"));
        if (query || ApplicationProject.IsReactor(component))
            context.ReportDiagnostic(Diagnostic.Create(Hydration, invocation.Syntax.GetLocation(), component.Name, method.Name));
    }

    static bool IsComponentRegistration(IMethodSymbol method) =>
        SymbolNames.Is(method.ContainingType, "Cntryl.Portia.PortiaBuilder")
        && method.Name is "AddRequestHandler" or "AddProjector" or "AddReactor" or "AddRequestAuthorizer"
            or "AddRequestGuard" or "AddRequestPipelineBehavior"
        || SymbolNames.Is(method.ContainingType, "Cntryl.Portia.CassiePortiaBuilderExtensions")
        && method.Name == "AddCassieProjection";

    static string? FeatureOf(ITypeSymbol? symbol)
    {
        for (var space = symbol?.ContainingNamespace; space is { IsGlobalNamespace: false }; space = space.ContainingNamespace)
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
            || method.ContainingType.AllInterfaces.Any(iface => SymbolNames.Is(iface, contract)));
    }

    static void AnalyzeConnectionConstruction(OperationAnalysisContext context, ApplicationProject project)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is INamedTypeSymbol type && (SymbolNames.Is(type, "Cntryl.Portia.CassieProjectionDatabase")
            || SymbolNames.Is(type, "Cntryl.Cassie.Connection.CassieDataSource")))
        {
            context.ReportDiagnostic(Diagnostic.Create(Dependency, creation.Syntax.GetLocation(), project.Name, type.Name));
        }
    }

    static void AnalyzeStub(SyntaxNodeAnalysisContext context)
    {
        var declaration = (MethodDeclarationSyntax)context.Node;
        var expression = declaration.ExpressionBody?.Expression is ThrowExpressionSyntax thrown ? thrown.Expression
            : declaration.Body is { Statements.Count: 1 } body && body.Statements[0] is ThrowStatementSyntax statement
                ? statement.Expression : null;
        if (expression is null || context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type is not INamedTypeSymbol exception
            || !SymbolNames.Is(exception, "System.NotImplementedException")
            || context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } method)
        {
            return;
        }

        var handler = method.ContainingType;
        var commandImplementation = ApplicationProject.Requests(handler).Any(request =>
            ApplicationProject.IsContractIn(request.TypeArguments[0], "Commands")
            && request.GetMembers("HandleAsync").Any(member => SymbolEqualityComparer.Default.Equals(
                handler.FindImplementationForInterfaceMember(member), method)));
        if (commandImplementation)
            context.ReportDiagnostic(Diagnostic.Create(CommandStub, declaration.Identifier.GetLocation(), handler.Name));
    }
}

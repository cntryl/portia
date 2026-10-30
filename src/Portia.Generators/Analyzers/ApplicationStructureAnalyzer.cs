using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia;

/// <summary>Checks the explicit Common/Core/Api/Worker application layout.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ApplicationStructureAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor OneType = new("PORTIA200", "File declares multiple types",
        "File '{0}' declares more than one type; put each type in its own file", "Portia", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor FileName = new("PORTIA201", "Filename does not identify its type",
        "Type '{0}' belongs in '{1}'; generic filenames use the OfT suffix", "Portia", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor Namespace = new("PORTIA202", "Namespace does not follow folders",
        "Type '{0}' belongs in namespace '{1}'", "Portia", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor FeaturePath = new("PORTIA203", "File is outside the closed feature layout",
        "File '{0}' must use Features/{{Feature}}/{{Role}} or the single Core feature registration file; allowed roles: {1}",
        "Portia", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor FolderRole = new("PORTIA204", "Type does not belong in its folder",
        "Type '{0}' does not belong in '{1}'; {2}", "Portia", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor HandlerRequests = new("PORTIA205", "Handler handles multiple requests",
        "Handler '{0}' handles more than one request; use one handler per command or query", "Portia", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor AggregateSize = new("PORTIA208", "Aggregate file exceeds the review budget",
        "Aggregate file '{0}' exceeds 400 lines; compose smaller rule or state objects to keep the aggregate reviewable",
        "Portia", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor ProjectRole = new("PORTIA213", "Unsupported application project role",
        "Application project '{0}' must end in .Common, .Core, .Api, or .Worker; mark test projects with IsTestProject",
        "Portia", DiagnosticSeverity.Warning, true, customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    static readonly string[] CommonFolders = ["Commands", "Queries", "Events", "Models", "Values"];
    static readonly string[] CoreFolders =
        ["Aggregates", "Handlers", "Reactors", "Projectors", "Repositories", "Authorization", "Ports", "Values"];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [OneType, FileName, Namespace, FeaturePath, FolderRole, HandlerRequests, AggregateSize, ProjectRole];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (ApplicationProject.From(start.Options.AnalyzerConfigOptionsProvider) is not { } project)
                return;
            if (!project.IsTest && project.Role is not ("Common" or "Core" or "Api" or "Worker"))
            {
                start.RegisterCompilationEndAction(end => end.ReportDiagnostic(
                    Diagnostic.Create(ProjectRole, Location.None, project.Name)));
            }

            start.RegisterSemanticModelAction(model => AnalyzeFile(model, project));
        });
    }

    static void AnalyzeFile(SemanticModelAnalysisContext context, ApplicationProject project)
    {
        var tree = context.SemanticModel.SyntaxTree;
        var root = tree.GetRoot(context.CancellationToken);
        var declarations = root.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax).ToArray();
        if (declarations.Length == 0)
            return;
        var path = project.RelativePath(tree);
        var filename = tree.FilePath.Replace('\\', '/').Split('/').Last();
        var keeper = declarations.FirstOrDefault(declaration => DeclaredFilename(declaration) == filename) ?? declarations[0];
        foreach (var declaration in declarations.Where(declaration => declaration != keeper))
        {
            var type = context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) as INamedTypeSymbol;
            var properties = ImmutableDictionary<string, string?>.Empty.Add("ExpectedFileName",
                type?.Name + (type?.Arity > 0 ? "OfT" : "") + ".cs");
            context.ReportDiagnostic(Diagnostic.Create(OneType, Identifier(declaration).GetLocation(), properties, filename));
        }

        var folders = project.Role == "Common" ? CommonFolders : CoreFolders;
        var featureProject = !project.IsTest && project.Role is "Common" or "Core";
        var featureFile = path.Length == 4 && path[0] == "Features" && Array.IndexOf(folders, path[2]) >= 0;
        var registrationFile = project.Role == "Core" && path.Length == 3 && path[0] == "Features"
            && path[2] == path[1] + "PortiaExtensions.cs";
        if (featureProject && path.Length != 0 && !featureFile && !registrationFile)
        {
            context.ReportDiagnostic(Diagnostic.Create(FeaturePath, Identifier(declarations[0]).GetLocation(),
                filename, string.Join(", ", folders)));
        }

        foreach (var declaration in declarations)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol symbol)
                continue;
            var identifier = Identifier(declaration);
            var expectedFile = symbol.Name + (symbol.Arity > 0 ? "OfT" : "") + ".cs";
            if (declarations.Length == 1 && filename != expectedFile)
            {
                context.ReportDiagnostic(Diagnostic.Create(FileName, identifier.GetLocation(),
                    ImmutableDictionary<string, string?>.Empty.Add("ExpectedFileName", expectedFile), symbol.Name, expectedFile));
            }

            if (path.Length > 0)
            {
                var expectedNamespace = string.Join(".", new[] { project.RootNamespace }.Concat(path.Take(path.Length - 1)));
                if (symbol.ContainingNamespace.ToDisplayString() != expectedNamespace)
                {
                    var properties = ImmutableDictionary<string, string?>.Empty.Add("ExpectedNamespace", expectedNamespace)
                        .Add("Folders", string.Join("/", path.Take(path.Length - 1)));
                    context.ReportDiagnostic(Diagnostic.Create(Namespace, identifier.GetLocation(), properties, symbol.Name, expectedNamespace));
                }
            }

            if (featureProject && featureFile && !BelongsIn(symbol, path[2]))
            {
                context.ReportDiagnostic(Diagnostic.Create(FolderRole, identifier.GetLocation(), symbol.Name,
                    path[2], FolderResponsibility(path[2])));
            }
            if (featureProject && registrationFile && (!symbol.IsStatic || symbol.Name != path[1] + "PortiaExtensions"))
            {
                context.ReportDiagnostic(Diagnostic.Create(FolderRole, identifier.GetLocation(), symbol.Name,
                    path[1], "the feature registration file holds its static PortiaExtensions class"));
            }

            if (!project.IsTest && ApplicationProject.Requests(symbol).Select(request => request.TypeArguments[0])
                    .Distinct(SymbolEqualityComparer.Default).Skip(1).Any())
            {
                context.ReportDiagnostic(Diagnostic.Create(HandlerRequests, identifier.GetLocation(), symbol.Name));
            }

            if (!project.IsTest && ApplicationProject.DerivesFrom(symbol, "Aggregate"))
            {
                var text = tree.GetText(context.CancellationToken);
                var lines = text.Lines.Count - (text.Lines[text.Lines.Count - 1].Span.Length == 0 ? 1 : 0);
                if (lines > 400)
                    context.ReportDiagnostic(Diagnostic.Create(AggregateSize, identifier.GetLocation(), filename));
            }
        }
    }

    static SyntaxToken Identifier(SyntaxNode declaration) => declaration switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier,
        _ => throw new ArgumentException("Expected a type declaration", nameof(declaration))
    };

    static string DeclaredFilename(SyntaxNode declaration)
    {
        var parameters = declaration switch
        {
            TypeDeclarationSyntax type => type.TypeParameterList,
            DelegateDeclarationSyntax @delegate => @delegate.TypeParameterList,
            _ => null
        };
        return Identifier(declaration).ValueText + (parameters is not null ? "OfT" : "") + ".cs";
    }

    static bool BelongsIn(INamedTypeSymbol symbol, string folder)
    {
        var request = ApplicationProject.IsRequest(symbol);
        var domainEvent = ApplicationProject.DerivesFrom(symbol, "DomainEvent");
        var handler = ApplicationProject.Requests(symbol).Length != 0;
        var reactor = ApplicationProject.IsReactor(symbol);
        var projector = ApplicationProject.IsProjector(symbol);
        var aggregate = ApplicationProject.DerivesFrom(symbol, "Aggregate");
        var authorizer = symbol.AllInterfaces.Any(iface => PortiaComponentRoles.Is(iface, PortiaComponentRoles.Authorizer));
        var component = PortiaComponentRoles.IsComponent(symbol);
        var plain = !request && !domainEvent && !component && !reactor && !projector && !aggregate;
        return folder switch
        {
            "Commands" or "Queries" => request && !component && !domainEvent,
            "Events" => domainEvent && !component,
            "Handlers" => handler && !reactor && !projector,
            "Reactors" => reactor && !handler && !projector,
            "Projectors" => projector && !handler && !reactor,
            "Authorization" => authorizer && !handler && !reactor && !projector,
            // Rule/state collaborators are legitimate aggregate composition. Ownership, as opposed
            // to a known misplaced framework component, needs review rather than a guessed marker.
            "Aggregates" => aggregate || plain,
            "Ports" => symbol.TypeKind == TypeKind.Interface && plain,
            "Models" or "Repositories" or "Values" => plain,
            _ => false
        };
    }

    static string FolderResponsibility(string folder) => folder switch
    {
        "Commands" or "Queries" => "only request contracts belong here",
        "Events" => "only domain event contracts belong here",
        "Handlers" => "only request handlers belong here",
        "Reactors" => "only reactors belong here",
        "Projectors" => "only projectors belong here",
        "Authorization" => "only request authorizers belong here",
        "Aggregates" => "aggregate roots and their owned rule/state collaborators belong here",
        "Ports" => "a port is an application interface to a production dependency",
        _ => "move framework component roles to the folder that owns them"
    };
}

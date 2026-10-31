using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia;

/// <summary>Checks the explicit Common/Core/Api/Worker application layout.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ApplicationStructureAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor OneType = new("PORTIA200", "File declares multiple types",
        "File '{0}' declares more than one type; put each type in its own file", "PortiaStructure", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor FileName = new("PORTIA201", "Filename does not identify its type",
        "Type '{0}' belongs in '{1}'; generic filenames use the OfT suffix", "PortiaStructure", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor Namespace = new("PORTIA202", "Namespace does not follow folders",
        "Type '{0}' belongs in namespace '{1}'", "PortiaStructure", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor FeaturePath = new("PORTIA203", "File is outside the closed feature layout",
        "File '{0}' is outside the feature layout; {1}",
        "PortiaStructure", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor FolderRole = new("PORTIA204", "Type does not belong in its folder",
        "Type '{0}' does not belong in '{1}'; {2}", "PortiaStructure", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor HandlerRequests = new("PORTIA205", "Handler handles multiple requests",
        "Handler '{0}' handles more than one request; use one handler per command or query", "PortiaStructure", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor AggregateSize = new("PORTIA208", "Aggregate file exceeds the review budget",
        "Aggregate file '{0}' exceeds 400 lines; compose smaller rule or state objects to keep the aggregate reviewable",
        "PortiaStructure", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor ProjectRole = new("PORTIA213", "Unsupported application project role",
        "Application project '{0}' must end in .Common, .Core, .Api, or .Worker; mark test projects with IsTestProject",
        "PortiaStructure", DiagnosticSeverity.Warning, true, customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    static readonly string[] CommonFolders = ["Commands", "Queries", "Events", "Models", "Values"];
    static readonly string[] CoreFolders =
        ["Aggregates", "Handlers", "Capabilities", "Reactors", "Projectors", "Repositories", "Authorization", "Events",
            "Ports", "Values", "Models", "Execution", "Sources", "Actions", "Errors", "Serialization"];
    static readonly string[] CoreSharedFolders = ["Events", "Execution", "Hosting"];

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

            var aggregateOwnership = new AggregateOwnershipIndex(start.Compilation, project);
            start.RegisterSemanticModelAction(model => AnalyzeFile(model, project, aggregateOwnership));
        });
    }

    static void AnalyzeFile(SemanticModelAnalysisContext context, ApplicationProject project,
        AggregateOwnershipIndex aggregateOwnership)
    {
        var tree = context.SemanticModel.SyntaxTree;
        var root = tree.GetRoot(context.CancellationToken);
        var declarations = root.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Where(node => node.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).ToArray();
        if (declarations.Length == 0)
            return;
        var path = project.RelativePath(tree);
        var filename = tree.FilePath.Replace('\\', '/').Split('/').Last();
        var keeper = declarations.FirstOrDefault(declaration => MatchesFilename(declaration, filename,
            context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) as INamedTypeSymbol)) ?? declarations[0];
        foreach (var declaration in declarations.Where(declaration => declaration != keeper))
        {
            var type = context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) as INamedTypeSymbol;
            var properties = ImmutableDictionary<string, string?>.Empty.Add("ExpectedFileName", Filename(type));
            context.ReportDiagnostic(Diagnostic.Create(OneType, Identifier(declaration).GetLocation(), properties, filename));
        }

        var folders = project.Role == "Common" ? CommonFolders : CoreFolders;
        var featureProject = !project.IsTest && project.Role is "Common" or "Core";
        var featureFile = path.Length >= 4 && path[0] == "Features" && Array.IndexOf(folders, path[2]) >= 0;
        var registrationFile = project.Role == "Core" && path.Length == 3 && path[0] == "Features"
            && path[2] == path[1] + "PortiaExtensions.cs";
        var featureRegistrationFile = project.Role == "Core" && path.Length == 2 && path[0] == "Features"
            && declarations.All(declaration => context.SemanticModel.GetDeclaredSymbol(declaration,
                context.CancellationToken) is INamedTypeSymbol type && type.IsStatic
                && type.Name.EndsWith("PortiaExtensions", StringComparison.Ordinal));
        var featureJsonContextFile = project.Role == "Core" && path.Length == 3 && path[0] == "Features"
            && declarations.All(declaration => context.SemanticModel.GetDeclaredSymbol(declaration,
                context.CancellationToken) is INamedTypeSymbol type && IsPortiaJsonContext(type)
                && JsonContextShape.UnsupportedReason(type) is null);
        var sharedCoreFile = project.Role == "Core" && path.Length >= 2
            && Array.IndexOf(CoreSharedFolders, path[0]) >= 0;
        var assemblyContextFile = path.Length == 1 && declarations.All(declaration =>
            context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is INamedTypeSymbol type
            && IsPortiaJsonContext(type)
            && JsonContextShape.UnsupportedReason(type) is null);
        if (featureProject && path.Length != 0 && !featureFile && !registrationFile && !featureRegistrationFile
            && !featureJsonContextFile && !sharedCoreFile && !assemblyContextFile)
        {
            var detail = path.Length >= 3 && path[0] == "Features"
                    ? $"'{path[2]}' is not a supported role; allowed roles: {string.Join(", ", folders)}"
                    : $"use Features/{{Feature}}/{{Role}}; allowed roles: {string.Join(", ", folders)}";
            context.ReportDiagnostic(Diagnostic.Create(FeaturePath, Identifier(declarations[0]).GetLocation(), filename, detail));
        }

        foreach (var declaration in declarations)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol symbol)
                continue;
            var identifier = Identifier(declaration);
            var expectedFile = Filename(symbol);
            if (declarations.Length == 1 && !MatchesFilename(declaration, filename, symbol))
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

            if (featureProject && featureFile
                && !BelongsIn(symbol, path[2], project, path[1], aggregateOwnership, context.CancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(FolderRole, identifier.GetLocation(), symbol.Name,
                    path[2], project.Role == "Common" && path[2] == "Values" && symbol.TypeKind == TypeKind.Enum
                        ? "enums are data models; place this enum in the feature's Models folder"
                        : FolderResponsibility(path[2])));
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

    static string Filename(string name, int arity)
    {
        var suffix = arity == 1 ? "OfT" : arity > 1 ? $"OfT{arity}" : "";
        return name + (suffix.Length != 0 && !name.EndsWith(suffix, StringComparison.Ordinal) ? suffix : "") + ".cs";
    }

    static string Filename(INamedTypeSymbol? symbol) => symbol is null ? ".cs" : Filename(symbol.Name, symbol.Arity);

    static bool MatchesFilename(SyntaxNode declaration, string filename, INamedTypeSymbol? symbol)
    {
        var arity = declaration switch
        {
            TypeDeclarationSyntax type => type.TypeParameterList?.Parameters.Count ?? 0,
            DelegateDeclarationSyntax type => type.TypeParameterList?.Parameters.Count ?? 0,
            _ => 0
        };
        var expected = Filename(Identifier(declaration).ValueText, arity);
        if (filename == expected)
            return true;
        return declaration is TypeDeclarationSyntax typeDeclaration && symbol?.DeclaringSyntaxReferences.Length > 1
            && typeDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword)
            && filename.StartsWith(expected.Substring(0, expected.Length - 3) + ".", StringComparison.Ordinal)
            && filename.EndsWith(".cs", StringComparison.Ordinal)
            && filename.Length > expected.Length + 1;
    }

    static bool BelongsIn(INamedTypeSymbol symbol, string folder, ApplicationProject project, string feature,
        AggregateOwnershipIndex aggregateOwnership, CancellationToken cancellationToken)
    {
        var request = ApplicationProject.IsRequest(symbol);
        var domainEvent = ApplicationProject.DerivesFrom(symbol, "DomainEvent");
        var handler = ApplicationProject.Requests(symbol).Length != 0;
        var reactor = ApplicationProject.IsReactor(symbol);
        var projector = ApplicationProject.IsProjector(symbol);
        var aggregate = ApplicationProject.DerivesFrom(symbol, "Aggregate");
        var authorizer = symbol.AllInterfaces.Any(iface => PortiaComponentRoles.Is(iface, PortiaComponentRoles.Authorizer));
        var executionComponent = symbol.AllInterfaces.Any(iface => PortiaComponentRoles.Is(iface, PortiaComponentRoles.Guard)
            || PortiaComponentRoles.Is(iface, PortiaComponentRoles.Behavior));
        var component = PortiaComponentRoles.IsComponent(symbol);
        var plain = !request && !domainEvent && !component && !reactor && !projector && !aggregate;
        return folder switch
        {
            "Commands" or "Queries" => request && !component && !domainEvent,
            "Events" => domainEvent && !component || feature == "Platform" && ApplicationProject.IsDomainEventCatalogAdapter(symbol),
            "Handlers" or "Capabilities" => handler && !reactor && !projector,
            "Reactors" => reactor && !handler && !projector,
            "Projectors" => projector && !handler && !reactor,
            "Authorization" => authorizer && !handler && !reactor && !projector,
            "Aggregates" => aggregate || plain && aggregateOwnership.Contains(feature, symbol, cancellationToken),
            "Ports" => symbol.TypeKind == TypeKind.Interface && plain,
            "Models" => plain && IsDataOnly(symbol),
            "Repositories" => plain && (symbol.TypeKind == TypeKind.Interface || IsRepository(symbol, project, feature)),
            "Values" => plain && IsImmutableValue(symbol),
            "Execution" => reactor && !handler && !projector
                || executionComponent && !handler && !reactor && !projector && !authorizer
                || plain && symbol.TypeKind == TypeKind.Class && !IsDataOnly(symbol)
                    && !IsErrorType(symbol) && !IsActionType(symbol) && !IsSourceType(symbol)
                    && !aggregateOwnership.Contains(feature, symbol, cancellationToken),
            "Sources" => plain && symbol.TypeKind == TypeKind.Class && !symbol.IsRecord && IsSourceType(symbol),
            "Actions" => plain && IsActionType(symbol),
            "Errors" => plain && IsErrorType(symbol),
            "Serialization" => IsPortiaJsonContext(symbol),
            _ => false
        };
    }

    static bool IsPortiaJsonContext(INamedTypeSymbol symbol) => symbol.GetAttributes().Any(attribute =>
        attribute.AttributeClass is { } marker && SymbolNames.Is(marker, "Cntryl.Portia.PortiaJsonContextAttribute"));

    static bool IsActionType(INamedTypeSymbol symbol)
    {
        if (symbol.Name.EndsWith("Action", StringComparison.Ordinal)
            && (symbol.TypeKind == TypeKind.Enum || symbol.IsRecord))
            return true;
        if (!symbol.IsStatic || !(symbol.Name.EndsWith("Actions", StringComparison.Ordinal)
            || symbol.Name.EndsWith("Operations", StringComparison.Ordinal)))
            return false;
        var members = symbol.GetMembers().Where(member => !member.IsImplicitlyDeclared).ToArray();
        return members.Length > 0 && members.All(member => member is IFieldSymbol
        {
            IsConst: true,
            Type.SpecialType: SpecialType.System_String
        });
    }

    static bool IsErrorType(INamedTypeSymbol symbol) => symbol.Name.EndsWith("Errors", StringComparison.Ordinal)
        || symbol.Name.EndsWith("ErrorCodes", StringComparison.Ordinal);

    static bool IsSourceType(INamedTypeSymbol symbol) => symbol.Name.EndsWith("Source", StringComparison.Ordinal)
        || symbol.Name.EndsWith("Sources", StringComparison.Ordinal);

    static bool IsDataOnly(INamedTypeSymbol symbol)
    {
        if (symbol.TypeKind == TypeKind.Interface)
        {
            var interfaces = symbol.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                .OfType<InterfaceDeclarationSyntax>().ToArray();
            return interfaces.Length > 0 && interfaces.All(declaration => declaration.Members.All(member =>
                member is PropertyDeclarationSyntax property
                    && property.ExpressionBody is null
                    && property.AccessorList is { Accessors.Count: > 0 } accessors
                    && accessors.Accessors.All(accessor => accessor.Keyword.IsKind(SyntaxKind.GetKeyword)
                        && accessor.Body is null && accessor.ExpressionBody is null)));
        }

        if (symbol.TypeKind == TypeKind.Enum)
            return symbol.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                .OfType<EnumDeclarationSyntax>().All(declaration => declaration.Members.Count > 0);
        if (symbol.TypeKind is not (TypeKind.Class or TypeKind.Struct)
            || symbol.DeclaringSyntaxReferences.Length == 0)
            return false;

        var declarations = symbol.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
            .OfType<TypeDeclarationSyntax>().ToArray();
        return declarations.Length > 0 && declarations.All(declaration => declaration.Members.All(member => member switch
            {
                FieldDeclarationSyntax field => field.Modifiers.Any(SyntaxKind.ConstKeyword)
                    || !field.Modifiers.Any(SyntaxKind.StaticKeyword)
                    && field.Declaration.Variables.All(variable => variable.Initializer is null),
                PropertyDeclarationSyntax property => !property.Modifiers.Any(SyntaxKind.StaticKeyword)
                    && property.ExpressionBody is null
                    && property.Initializer is null
                    && property.AccessorList is { Accessors.Count: > 0 } accessors
                    && accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null),
                MethodDeclarationSyntax method => IsToStringOverride(method),
                _ => false
            })) && declarations.Any(declaration => declaration.Members.Any(member =>
                member is FieldDeclarationSyntax or PropertyDeclarationSyntax)
                || declaration is RecordDeclarationSyntax { ParameterList.Parameters.Count: > 0 });
    }

    static bool IsImmutableValue(INamedTypeSymbol symbol) => symbol.IsRecord
        && symbol.GetMembers().Where(member => !member.IsImplicitlyDeclared).All(member => member switch
        {
            IFieldSymbol field => field.IsConst || field.IsReadOnly,
            IPropertySymbol property => property.SetMethod is null || property.SetMethod.IsInitOnly,
            _ => true
        });

    static bool IsToStringOverride(MethodDeclarationSyntax method) => method.Modifiers.Any(SyntaxKind.OverrideKeyword)
        && method.Identifier.ValueText == nameof(ToString) && method.ParameterList.Parameters.Count == 0;
    static bool IsRepository(INamedTypeSymbol symbol, ApplicationProject project, string feature) =>
        ApplicationProject.DerivesFrom(symbol, "CassieProjectionRepository")
        || symbol.AllInterfaces.Any(iface => SymbolNames.Is(iface, "Cntryl.Portia.IProjectionStore")
            || SymbolNames.Is(iface, "Cntryl.Portia.IProjectionCheckpointStore"))
        || symbol.AllInterfaces.Any(iface => iface.DeclaringSyntaxReferences.Any(reference =>
        {
            var path = project.RelativePath(reference.SyntaxTree);
            return path.Length >= 4 && path[0] == "Features" && path[1] == feature;
        }));

    sealed class AggregateOwnershipIndex(Compilation compilation, ApplicationProject project)
    {
        readonly object _gate = new();
        ImmutableDictionary<string, ImmutableHashSet<INamedTypeSymbol>>? _byFeature;

        public bool Contains(string feature, INamedTypeSymbol target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!Monitor.TryEnter(_gate, 50))
                cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _byFeature ??= Build(cancellationToken);
                return _byFeature.TryGetValue(feature, out var types) && types.Contains(target.OriginalDefinition);
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        ImmutableDictionary<string, ImmutableHashSet<INamedTypeSymbol>> Build(CancellationToken cancellationToken)
        {
            var features = new Dictionary<string, HashSet<INamedTypeSymbol>>(StringComparer.Ordinal);
            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = project.RelativePath(tree);
                if (path.Length != 4 || path[0] != "Features" || path[2] != "Aggregates")
                    continue;
#pragma warning disable RS1030 // Ownership requires binding aggregate references in other source trees.
                var model = compilation.GetSemanticModel(tree);
#pragma warning restore RS1030
                foreach (var declaration in tree.GetRoot(cancellationToken).DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol aggregate
                        || !ApplicationProject.DerivesFrom(aggregate, "Aggregate"))
                        continue;
                    if (!features.TryGetValue(path[1], out var types))
                        features.Add(path[1], types = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));
                    foreach (var node in declaration.DescendantNodes().OfType<SimpleNameSyntax>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var referenced = model.GetAliasInfo(node, cancellationToken)?.Target
                            ?? model.GetSymbolInfo(node, cancellationToken).Symbol;
                        var type = referenced as INamedTypeSymbol ?? referenced?.ContainingType;
                        if (type is not null)
                            _ = types.Add(type.OriginalDefinition);
                    }
                }
            }
            return features.ToImmutableDictionary(pair => pair.Key,
                pair => pair.Value.ToImmutableHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default), StringComparer.Ordinal);
        }
    }

    static string FolderResponsibility(string folder) => folder switch
    {
        "Commands" or "Queries" => "only request contracts belong here",
        "Events" => "domain event contracts and their generated catalog adapter belong here",
        "Handlers" => "only request handlers belong here",
        "Capabilities" => "only request handlers for feature capabilities belong here",
        "Reactors" => "only reactors belong here",
        "Projectors" => "only projectors belong here",
        "Authorization" => "only request authorizers belong here",
        "Aggregates" => "aggregate roots and their directly owned rule or state collaborators belong here",
        "Ports" => "a port is an application interface to a production dependency",
        "Models" => "data-only models belong here",
        "Values" => "immutable record values belong here",
        "Execution" => "execution coordination and unowned behavior helpers belong here; directly owned aggregate helpers belong with their root",
        "Sources" => "feature source helpers belong here",
        "Actions" => "feature action types and operation catalogs belong here",
        "Errors" => "feature error factories belong here",
        "Serialization" => "source-generated JSON contexts belong here",
        _ => "move framework component roles to the folder that owns them"
    };
}

using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Adds missing Portia serializer roots to an application-owned JSON context.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(JsonMetadataCodeFixProvider))]
[Shared]
public sealed class JsonMetadataCodeFixProvider : CodeFixProvider
{
    const string CreateContextKey = "Portia.CreateJsonContext";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["PORTIA025"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => JsonMetadataFixAllProvider.Instance;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics.First();
        if (!diagnostic.Properties.TryGetValue("TypeName", out var typeName) || typeName is null)
        {
            return;
        }

        var contexts = await FindContextsAsync(context.Document.Project, context.CancellationToken)
            .ConfigureAwait(false);
        if (contexts.Length == 0)
        {
            var contextNamespace = diagnostic.Properties.TryGetValue("Namespace", out var value)
                ? value ?? string.Empty
                : NamespaceOf(typeName);
            context.RegisterCodeFix(CodeAction.Create($"Create a Portia JSON context for {typeName}",
                    ct => CreateContextAsync(context.Document.Project, typeName, contextNamespace, ct),
                    CreateContextKey),
                diagnostic);
            return;
        }

        var rootNamespace = NamespaceOf(typeName);
        var matching = contexts.Where(item => item.Namespace == rootNamespace).ToImmutableArray();
        var choices = contexts.Length == 1 ? contexts : matching.Length == 1 ? matching : contexts;
        foreach (var choice in choices)
        {
            var title = contexts.Length == 1 || matching.Length == 1
                ? $"Add {typeName} to {choice.Name}"
                : $"Add {typeName} to {choice.Name} ({choice.Namespace})";
            context.RegisterCodeFix(CodeAction.Create(title,
                ct => AddRootAsync(context.Document.Project.Solution, choice, typeName, ct),
                EquivalenceKey(choice)), diagnostic);
        }
    }

    // A partial context is one fix however many files declare it. Its roots go on the part carrying
    // [PortiaJsonContext] unless that part is generated code its tool would overwrite; then they go on the
    // first part the application wrote. A context with no hand-written part is not offered.
    static async Task<ImmutableArray<ContextInfo>> FindContextsAsync(Project project,
        CancellationToken cancellationToken)
    {
        var chosen = new Dictionary<INamedTypeSymbol, (ContextInfo Info, bool Marker)>(SymbolEqualityComparer.Default);
        var order = new List<INamedTypeSymbol>();
        foreach (var document in project.Documents)
        {
            if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax
                    root || IsGenerated(document, root))
            {
                continue;
            }

            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model?.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol
                    || symbol.GetAttributes().FirstOrDefault(a =>
                            a.AttributeClass?.ToDisplayString() == "Cntryl.Portia.PortiaJsonContextAttribute")
                        is not { } marker)
                {
                    continue;
                }

                var isMarker = marker.ApplicationSyntaxReference is { } reference
                               && reference.SyntaxTree == declaration.SyntaxTree
                               && declaration.AttributeLists.Any(list => list.Span.Contains(reference.Span));
                if (chosen.TryGetValue(symbol, out var existing) && (existing.Marker || !isMarker))
                {
                    continue;
                }

                if (!chosen.ContainsKey(symbol))
                    order.Add(symbol);
                chosen[symbol] = (new ContextInfo(document.Id, declaration.SpanStart, symbol.Name,
                    symbol.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : symbol.ContainingNamespace.ToDisplayString()), isMarker);
            }
        }

        return [.. order.Select(symbol => chosen[symbol].Info)];
    }

    static bool IsGenerated(Document document, CompilationUnitSyntax root)
    {
        var name = System.IO.Path.GetFileName(document.FilePath ?? document.Name);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
               || root.GetLeadingTrivia().Any(trivia =>
                   trivia.ToString().IndexOf("<auto-generated", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    static async Task<Solution> AddRootAsync(Solution solution, ContextInfo context, string typeName,
        CancellationToken cancellationToken) =>
        await AddRootsAsync(solution, context, [typeName], cancellationToken).ConfigureAwait(false);

    static async Task<Solution> AddRootsAsync(Solution solution, ContextInfo context,
        IEnumerable<string> typeNames, CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(context.DocumentId)!;
        var root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .First(node => node.SpanStart == context.SpanStart);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        // Read through the symbol, so a root declared on any partial part of the context counts.
        var existing = (semanticModel?.GetDeclaredSymbol(declaration, cancellationToken)?.GetAttributes() ?? [])
            .Where(attribute => attribute.AttributeClass?.ToDisplayString()
                                == "System.Text.Json.Serialization.JsonSerializableAttribute")
            .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value)
            .OfType<ITypeSymbol>()
            .Select(type => NormalizeTypeName(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
            .ToImmutableHashSet(StringComparer.Ordinal);
        var attributes = Distinct(typeNames)
            .Where(typeName => !existing.Contains(NormalizeTypeName(typeName)))
            .Select(typeName => SyntaxFactory.AttributeList([
                SyntaxFactory.Attribute(
                    SyntaxFactory.ParseName("System.Text.Json.Serialization.JsonSerializable"),
                    SyntaxFactory.AttributeArgumentList([
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(GlobalTypeName(typeName))))
                    ]))
            ])).ToArray();
        if (attributes.Length == 0)
            return solution;

        var updated = declaration.AddAttributeLists(attributes);
        return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(declaration, updated));
    }

    static async Task<Solution> CreateContextAsync(Project project, string typeName, string contextNamespace,
        CancellationToken cancellationToken) =>
        await CreateContextAsync(project, [new RootInfo(typeName, contextNamespace)], cancellationToken)
            .ConfigureAwait(false);

    static async Task<Solution> CreateContextAsync(Project project, IReadOnlyCollection<RootInfo> roots,
        CancellationToken cancellationToken)
    {
        var contextNamespace = ContextNamespace(project, roots);
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        var contextName = AvailableContextName(compilation, contextNamespace);
        var namespaceDeclaration = string.IsNullOrEmpty(contextNamespace)
            ? string.Empty
            : $"namespace {contextNamespace};\n\n";
        var rootAttributes = string.Join(string.Empty, Distinct(roots.Select(root => root.TypeName))
            .Select(typeName => $"[JsonSerializable(typeof({GlobalTypeName(typeName)}))]\n"));
        var source = "using System.Text.Json;\nusing System.Text.Json.Serialization;\nusing Cntryl.Portia;\n\n"
                     + namespaceDeclaration
                     + "[PortiaJsonContext]\n[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]\n"
                     + rootAttributes
                     + "internal sealed partial class PortiaJsonContext : JsonSerializerContext;\n";
        source = source.Replace("class PortiaJsonContext", $"class {contextName}");
        var document = project.AddDocument($"{contextName}.cs", source);
        _ = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return document.Project.Solution;
    }

    static string AvailableContextName(Compilation? compilation, string contextNamespace)
    {
        if (Available("PortiaJsonContext"))
            return "PortiaJsonContext";
        if (Available("ApplicationJsonContext"))
            return "ApplicationJsonContext";
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"PortiaJsonContext{suffix}";
            if (Available(candidate))
                return candidate;
        }

        bool Available(string candidate)
        {
            var metadataName = string.IsNullOrEmpty(contextNamespace)
                ? candidate
                : $"{contextNamespace}.{candidate}";
            return compilation?.GetTypeByMetadataName(metadataName) is null;
        }
    }

    static string NamespaceOf(string typeName)
    {
        var value = typeName.Replace("global::", string.Empty);
        var index = value.LastIndexOf('.');
        return index < 0 ? string.Empty : value.Substring(0, index);
    }

    static string ContextNamespace(Project project, IReadOnlyCollection<RootInfo> roots)
    {
        var namespaces = roots.Select(root => root.Namespace).Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (namespaces.Length == 1)
            return namespaces[0];
        var defaultNamespace = project.DefaultNamespace;
        if (defaultNamespace is { Length: > 0 }
            && !SyntaxFactory.ParseName(defaultNamespace).ContainsDiagnostics)
        {
            return defaultNamespace;
        }

        return string.Empty;
    }

    static string NormalizeTypeName(string typeName) => typeName.Replace("global::", string.Empty);

    // One name per root, compared without qualification, in a stable order.
    static IEnumerable<string> Distinct(IEnumerable<string> typeNames) => typeNames
        .GroupBy(NormalizeTypeName, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => group.First());

    // A root is written global-qualified: a partially qualified name binds relative to the context's
    // namespace, where a same-named child namespace (App.Orders for Orders.Query) captures it. A name the
    // diagnostic already qualified keeps its qualified type arguments too.
    static string GlobalTypeName(string typeName) =>
        typeName.StartsWith("global::", StringComparison.Ordinal) ? typeName : "global::" + typeName;

    static string EquivalenceKey(ContextInfo context) =>
        $"Portia.AddJsonRoot.{context.DocumentId}.{context.SpanStart}";

    sealed class JsonMetadataFixAllProvider : FixAllProvider
    {
        public static readonly JsonMetadataFixAllProvider Instance = new();

        public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            IEnumerable<Diagnostic> diagnostics;
            if (fixAllContext.Scope == FixAllScope.Document && fixAllContext.Document is { } document)
            {
                diagnostics = await fixAllContext.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false);
            }
            else if (fixAllContext.Scope == FixAllScope.Project)
            {
                diagnostics = await fixAllContext.GetAllDiagnosticsAsync(fixAllContext.Project)
                    .ConfigureAwait(false);
            }
            else
            {
                // A selected context belongs to one project. Applying that choice to unrelated projects in a
                // solution would silently guess ownership, so Portia deliberately offers document/project scope.
                return null;
            }

            var roots = diagnostics.Select(RootInfo.From).Where(root => root is not null).Select(root => root!)
                .GroupBy(root => root.TypeName, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            if (roots.Length == 0)
                return null;

            return CodeAction.Create("Add all missing Portia JSON roots",
                ct => ApplyAsync(fixAllContext.Project, roots, fixAllContext.CodeActionEquivalenceKey, ct),
                fixAllContext.CodeActionEquivalenceKey);
        }

        static async Task<Solution> ApplyAsync(Project project, IReadOnlyCollection<RootInfo> roots,
            string? equivalenceKey, CancellationToken cancellationToken)
        {
            var contexts = await FindContextsAsync(project, cancellationToken).ConfigureAwait(false);
            if (equivalenceKey == CreateContextKey)
            {
                return contexts.Length == 0
                    ? await CreateContextAsync(project, roots, cancellationToken).ConfigureAwait(false)
                    : project.Solution;
            }

            var selected = contexts.FirstOrDefault(context => EquivalenceKey(context) == equivalenceKey);
            return selected is null
                ? project.Solution
                : await AddRootsAsync(project.Solution, selected, roots.Select(root => root.TypeName),
                    cancellationToken).ConfigureAwait(false);
        }
    }

    sealed class RootInfo(string typeName, string @namespace)
    {
        public string TypeName { get; } = typeName;
        public string Namespace { get; } = @namespace;

        public static RootInfo? From(Diagnostic diagnostic)
        {
            if (!diagnostic.Properties.TryGetValue("TypeName", out var typeName) || typeName is null)
                return null;
            var @namespace = diagnostic.Properties.TryGetValue("Namespace", out var value)
                ? value ?? string.Empty
                : NamespaceOf(typeName);
            return new RootInfo(typeName, @namespace);
        }
    }

    sealed class ContextInfo(DocumentId documentId, int spanStart, string name, string @namespace)
    {
        public DocumentId DocumentId { get; } = documentId;
        public int SpanStart { get; } = spanStart;
        public string Name { get; } = name;
        public string Namespace { get; } = @namespace;
    }
}

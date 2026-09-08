using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Adds missing Portia serializer roots to an application-owned JSON context.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(JsonMetadataCodeFixProvider)), Shared]
public sealed class JsonMetadataCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["PORTIA025"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics.First();
        if (!diagnostic.Properties.TryGetValue("TypeName", out var typeName) || typeName is null) return;
        var contexts = await FindContextsAsync(context.Document.Project, context.CancellationToken).ConfigureAwait(false);
        if (contexts.Length == 0)
        {
            context.RegisterCodeFix(CodeAction.Create($"Create PortiaJsonContext for {typeName}",
                ct => CreateContextAsync(context.Document.Project, typeName, ct), "Portia.CreateJsonContext"), diagnostic);
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
                $"Portia.AddJsonRoot.{choice.DocumentId}.{choice.SpanStart}"), diagnostic);
        }
    }

    static async Task<ImmutableArray<ContextInfo>> FindContextsAsync(Project project, CancellationToken cancellationToken)
    {
        var results = ImmutableArray.CreateBuilder<ContextInfo>();
        foreach (var document in project.Documents)
        {
            if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root) continue;
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model?.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol) continue;
                if (!symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Cntryl.Portia.PortiaJsonContextAttribute")) continue;
                results.Add(new ContextInfo(document.Id, declaration.SpanStart, symbol.Name,
                    symbol.ContainingNamespace.IsGlobalNamespace ? string.Empty : symbol.ContainingNamespace.ToDisplayString()));
            }
        }
        return results.ToImmutable();
    }

    static async Task<Solution> AddRootAsync(Solution solution, ContextInfo context, string typeName, CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(context.DocumentId)!;
        var root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(node => node.SpanStart == context.SpanStart);
        var text = $"System.Text.Json.Serialization.JsonSerializable(typeof({typeName.Replace("global::", string.Empty)}))";
        if (declaration.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => attribute.ToString() == text)) return solution;
        var attribute = SyntaxFactory.Attribute(SyntaxFactory.ParseName("System.Text.Json.Serialization.JsonSerializable"),
            SyntaxFactory.AttributeArgumentList([SyntaxFactory.AttributeArgument(
                SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(typeName.Replace("global::", string.Empty))))]));
        var updated = declaration.AddAttributeLists(SyntaxFactory.AttributeList([attribute]));
        return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(declaration, updated));
    }

    static async Task<Solution> CreateContextAsync(Project project, string typeName, CancellationToken cancellationToken)
    {
        var ns = project.DefaultNamespace ?? project.Name.Replace(" ", string.Empty);
        var source = "using System.Text.Json;\nusing System.Text.Json.Serialization;\nusing Cntryl.Portia;\n\n"
            + $"namespace {ns};\n\n[PortiaJsonContext]\n[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]\n"
            + $"[JsonSerializable(typeof({typeName.Replace("global::", string.Empty)}))]\ninternal sealed partial class PortiaJsonContext : JsonSerializerContext;\n";
        var document = project.AddDocument("PortiaJsonContext.cs", source);
        _ = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return document.Project.Solution;
    }

    static string NamespaceOf(string typeName)
    {
        var value = typeName.Replace("global::", string.Empty);
        var index = value.LastIndexOf('.');
        return index < 0 ? string.Empty : value.Substring(0, index);
    }

    sealed class ContextInfo(DocumentId documentId, int spanStart, string name, string @namespace)
    {
        public DocumentId DocumentId { get; } = documentId;
        public int SpanStart { get; } = spanStart;
        public string Name { get; } = name;
        public string Namespace { get; } = @namespace;
    }
}

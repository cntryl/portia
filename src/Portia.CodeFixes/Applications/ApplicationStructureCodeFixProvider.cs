using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>Offers previewable, mechanical fixes for application files, namespaces, and implicit schema versions.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ApplicationStructureCodeFixProvider))]
[Shared]
public sealed class ApplicationStructureCodeFixProvider : CodeFixProvider
{
    const string ConstructorReference = "Portia.NamespaceConstructorReference";
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["PORTIA200", "PORTIA201", "PORTIA202", "PORTIA210"];

    /// <inheritdoc />
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        if (await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root)
            return;
        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.Id is "PORTIA200" or "PORTIA201"
                && diagnostic.Properties.TryGetValue("ExpectedFileName", out var filename) && filename is not null
                && AvailableFile(context.Document, filename))
            {
                var declaration = root.FindNode(diagnostic.Location.SourceSpan).AncestorsAndSelf()
                    .OfType<MemberDeclarationSyntax>().FirstOrDefault(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
                if (declaration is null)
                    continue;
                if (diagnostic.Id == "PORTIA201")
                {
                    context.RegisterCodeFix(CodeAction.Create($"Rename file to '{filename}'",
                        _ => Task.FromResult(RenameFile(context.Document, filename)), "Portia.RenameApplicationFile"), diagnostic);
                }
                else if (declaration.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax
                    && !string.Equals(context.Document.Name, filename, StringComparison.OrdinalIgnoreCase)
                    && !root.ContainsDirectives && !root.DescendantTokens().Any(token => token.IsKind(SyntaxKind.FileKeyword)))
                {
                    var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
                    var name = model?.GetDeclaredSymbol(declaration, context.CancellationToken)?.Name;
                    context.RegisterCodeFix(CodeAction.Create($"Move '{name}' to '{filename}'",
                        ct => MoveTypeAsync(context.Document, root, declaration, filename, ct), "Portia.MoveApplicationType"), diagnostic);
                }
            }
            else if (diagnostic.Id == "PORTIA202")
            {
                await RegisterNamespaceFixAsync(context, diagnostic, root).ConfigureAwait(false);
            }
            else if (diagnostic.Id == "PORTIA210"
                && diagnostic.Properties.TryGetValue("ImplicitVersion", out var value)
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0
                && root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<AttributeSyntax>() is { } attribute)
            {
                context.RegisterCodeFix(CodeAction.Create($"Make schema version {value} explicit",
                    ct => MakeVersionExplicitAsync(context.Document, root, attribute, version, ct), "Portia.ExplicitSchemaVersion"), diagnostic);
            }
        }
    }

    static bool AvailableFile(Document document, string filename)
    {
        if (document.FilePath is { } path && document.Project.Solution.GetDocumentIdsWithFilePath(path).Length > 1)
            return false;
        var destination = SiblingPath(document.FilePath, filename);
        return !document.Project.Documents.Any(other => other.Id != document.Id
            && (destination is not null && string.Equals(other.FilePath, destination, StringComparison.OrdinalIgnoreCase)
                || string.Equals(other.Name, filename, StringComparison.OrdinalIgnoreCase)
                && other.Folders.SequenceEqual(document.Folders)));
    }

    static string? SiblingPath(string? path, string filename)
    {
        var slash = path?.LastIndexOfAny(['/', '\\']) ?? -1;
        return slash >= 0 ? path!.Substring(0, slash + 1) + filename : null;
    }

    static Solution RenameFile(Document document, string filename)
    {
        var solution = document.Project.Solution.WithDocumentName(document.Id, filename);
        return SiblingPath(document.FilePath, filename) is { } path
            ? solution.WithDocumentFilePath(document.Id, path) : solution;
    }

    static async Task<Solution> MoveTypeAsync(Document document, CompilationUnitSyntax root,
        MemberDeclarationSyntax declaration, string filename, CancellationToken ct)
    {
        var member = declaration;
        foreach (var space in declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
            member = space.WithMembers(SyntaxFactory.SingletonList(member));
        var movedRoot = root.WithAttributeLists(default).WithMembers(SyntaxFactory.SingletonList(member))
            .WithAdditionalAnnotations(Formatter.Annotation);
        var remaining = root.RemoveNode(declaration, SyntaxRemoveOptions.KeepNoTrivia)!;
        var id = DocumentId.CreateNewId(document.Project.Id);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var solution = document.Project.Solution.WithDocumentSyntaxRoot(document.Id, remaining)
            .AddDocument(id, filename, SourceText.From(movedRoot.ToFullString(), text.Encoding), document.Folders,
                SiblingPath(document.FilePath, filename));
        var formatted = await Formatter.FormatAsync(solution.GetDocument(id)!, Formatter.Annotation, cancellationToken: ct).ConfigureAwait(false);
        return formatted.Project.Solution;
    }

    static async Task RegisterNamespaceFixAsync(CodeFixContext context, Diagnostic diagnostic, CompilationUnitSyntax root)
    {
        if (!diagnostic.Properties.TryGetValue("ExpectedNamespace", out var target) || target is null
            || !diagnostic.Properties.TryGetValue("Folders", out var value) || value is null)
        {
            return;
        }

        var folders = value.Length == 0 ? [] : value.Split('/');
        var project = context.Document.Project;
        if (string.Join(".", new[] { project.DefaultNamespace }.Concat(folders)) != target)
            return;
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        // A move into an existing type with the same name is a design conflict, not a repair.
        foreach (var declaration in root.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
        {
            if (model?.GetDeclaredSymbol(declaration, context.CancellationToken) is INamedTypeSymbol symbol
                && model.Compilation.GetTypeByMetadataName(target + "." + symbol.MetadataName) is { } existing
                && !SymbolEqualityComparer.Default.Equals(symbol, existing))
            {
                return;
            }
        }

        // Use Roslyn's document namespace refactoring so references across the solution are updated.
        // A temporary logical folder makes the refactoring run even when the physical folders are correct.
        var prepared = await PrepareConstructorReferencesAsync(context.Document, root, model!, context.CancellationToken).ConfigureAwait(false);
        prepared = prepared.WithFolders(prepared.Folders.Concat(["PortiaNamespaceFix"]));
        var actions = await Renamer.RenameDocumentAsync(prepared, new DocumentRenameOptions(), prepared.Name,
            folders, context.CancellationToken).ConfigureAwait(false);
        if (actions.ApplicableActions.Length == 0 || actions.ApplicableActions.Any(action => action.GetErrors(CultureInfo.InvariantCulture).Any()))
            return;
        Solution corrected;
        try
        {
            corrected = await actions.UpdateSolutionAsync(prepared.Project.Solution, context.CancellationToken).ConfigureAwait(false);
            // Roslyn's namespace refactoring excludes constructor definitions from its type
            // reference results. Repair the qualified constructor references captured beforehand.
            foreach (var id in corrected.GetChanges(context.Document.Project.Solution).GetProjectChanges()
                         .SelectMany(change => change.GetChangedDocuments()))
            {
                var referenceRoot = await corrected.GetDocument(id)!.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
                var references = referenceRoot!.GetAnnotatedNodes(ConstructorReference).ToArray();
                if (references.Length == 0)
                    continue;
                var updatedRoot = referenceRoot.ReplaceNodes(references, (_, current) => current switch
                {
                    QualifiedNameSyntax qualified => qualified.WithLeft(SyntaxFactory.ParseName("global::" + target)
                        .WithTriviaFrom(qualified.Left)).WithoutAnnotations(ConstructorReference).WithAdditionalAnnotations(Simplifier.Annotation),
                    AliasQualifiedNameSyntax alias => SyntaxFactory.QualifiedName(SyntaxFactory.ParseName("global::" + target), alias.Name)
                        .WithTriviaFrom(alias).WithAdditionalAnnotations(Simplifier.Annotation),
                    _ => current
                });
                corrected = corrected.WithDocumentSyntaxRoot(id, updatedRoot);
            }
        }
        catch (InvalidOperationException)
        {
            // Minimal/custom hosts may provide syntax services without Roslyn's namespace
            // refactoring service. Do not offer an action that would fail when selected.
            return;
        }

        var changed = corrected.GetDocument(context.Document.Id)!;
        var changedRoot = await changed.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var changedModel = await changed.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (changedRoot is null || changedModel is null || changedRoot.DescendantNodes()
            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Any(node => changedModel.GetDeclaredSymbol(node, context.CancellationToken)?.ContainingNamespace.ToDisplayString() != target))
        {
            return;
        }

        context.RegisterCodeFix(CodeAction.Create($"Change namespace to '{target}' and update references",
            _ => Task.FromResult(corrected), "Portia.SyncApplicationNamespace"), diagnostic);
    }

    static async Task<Document> PrepareConstructorReferencesAsync(Document document, CompilationUnitSyntax root,
        SemanticModel model, CancellationToken ct)
    {
        var locations = new Dictionary<DocumentId, HashSet<TextSpan>>();
        foreach (var declaration in root.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
        {
            if (model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol symbol)
                continue;
            var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, ct).ConfigureAwait(false);
            foreach (var reference in references.Where(reference => reference.Definition is IMethodSymbol { MethodKind: MethodKind.Constructor }))
            {
                foreach (var location in reference.Locations)
                {
                    if (!locations.TryGetValue(location.Document.Id, out var spans))
                        locations.Add(location.Document.Id, spans = []);
                    _ = spans.Add(location.Location.SourceSpan);
                }
            }
        }

        var solution = document.Project.Solution;
        foreach (var pair in locations)
        {
            var referenceRoot = await solution.GetDocument(pair.Key)!.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var types = pair.Value.Select(span => referenceRoot!.FindNode(span).FirstAncestorOrSelf<ObjectCreationExpressionSyntax>()?.Type)
                .Where(type => type is QualifiedNameSyntax or AliasQualifiedNameSyntax).Distinct().Cast<TypeSyntax>().ToArray();
            solution = solution.WithDocumentSyntaxRoot(pair.Key, referenceRoot!.ReplaceNodes(types,
                (_, current) => current.WithAdditionalAnnotations(new SyntaxAnnotation(ConstructorReference))));
        }

        return solution.GetDocument(document.Id)!;
    }

    static async Task<Document> MakeVersionExplicitAsync(Document document, CompilationUnitSyntax root,
        AttributeSyntax attribute, int version, CancellationToken ct)
    {
        var argument = SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(version))).WithNameColon(SyntaxFactory.NameColon("version"));
        var updated = attribute.AddArgumentListArguments(argument).WithAdditionalAnnotations(Formatter.Annotation);
        var changed = document.WithSyntaxRoot(root.ReplaceNode(attribute, updated));
        return await Formatter.FormatAsync(changed, Formatter.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }
}

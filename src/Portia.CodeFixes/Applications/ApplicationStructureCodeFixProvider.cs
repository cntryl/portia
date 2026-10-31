using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
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
    public override FixAllProvider? GetFixAllProvider() => StructureFixAllProvider.Instance;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        if (await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root)
            return;
        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.Id is "PORTIA200" or "PORTIA201"
                && diagnostic.Properties.TryGetValue("ExpectedFileName", out var filename) && filename is not null
                && SupportsFileChanges(context.Document.Project.Solution.Workspace)
                && AvailableFile(context.Document, filename))
            {
                var declaration = root.FindNode(diagnostic.Location.SourceSpan).AncestorsAndSelf()
                    .OfType<MemberDeclarationSyntax>().FirstOrDefault(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
                if (declaration is null)
                    continue;
                var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
                if (model?.GetDeclaredSymbol(declaration, context.CancellationToken) is INamedTypeSymbol declared
                    && declared.DeclaringSyntaxReferences.Length > 1)
                    continue;
                if (diagnostic.Id == "PORTIA201")
                {
                    context.RegisterCodeFix(CodeAction.Create($"Rename file to '{filename}'",
                        ct => RenameFileAsync(context.Document, filename, ct), "Portia.RenameApplicationFile"), diagnostic);
                }
                else if (declaration.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax
                    && !string.Equals(context.Document.Name, filename, StringComparison.OrdinalIgnoreCase)
                    && SafeToSplit(root) && !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)
                    && !declaration.GetLeadingTrivia().Any(trivia => trivia.IsDirective))
                {
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

    // MSBuildWorkspace currently persists added documents as explicit Compile items, which
    // duplicates SDK default globs, and does not reliably apply document replacement renames.
    static bool SupportsFileChanges(Workspace workspace) =>
        workspace.GetType().FullName != "Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace";

    static bool SafeToSplit(CompilationUnitSyntax root) =>
        !root.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsDirective
            && (trivia.SpanStart >= root.GetFirstToken().SpanStart
                || !trivia.IsKind(SyntaxKind.NullableDirectiveTrivia)
                && !trivia.IsKind(SyntaxKind.PragmaWarningDirectiveTrivia)))
        && !root.DescendantTokens().Any(token => token.IsKind(SyntaxKind.FileKeyword));

    static CompilationUnitSyntax MovedRoot(CompilationUnitSyntax root, MemberDeclarationSyntax declaration)
    {
        var member = declaration;
        foreach (var space in declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
            member = space.WithMembers(SyntaxFactory.SingletonList(member));
        var moved = root.WithAttributeLists(default).WithMembers(SyntaxFactory.SingletonList(member));
        if (!moved.GetLeadingTrivia().Any(trivia => trivia.IsDirective))
        {
            var directives = string.Concat(root.GetLeadingTrivia().Where(trivia => trivia.IsDirective).Select(trivia => trivia.ToFullString()));
            if (directives.Length > 0)
                moved = moved.WithLeadingTrivia(SyntaxFactory.ParseLeadingTrivia(directives).AddRange(moved.GetLeadingTrivia()));
        }
        return moved.WithAdditionalAnnotations(Formatter.Annotation);
    }

    sealed class StructureFixAllProvider : FixAllProvider
    {
        public static readonly StructureFixAllProvider Instance = new();

        public override async Task<CodeAction?> GetFixAsync(FixAllContext context)
        {
            var id = context.CodeActionEquivalenceKey switch
            {
                "Portia.MoveApplicationType" => "PORTIA200",
                "Portia.RenameApplicationFile" => "PORTIA201",
                "Portia.SyncApplicationNamespace" => "PORTIA202",
                _ => null
            };
            if (id is null || context.Scope is not (FixAllScope.Document or FixAllScope.Project or FixAllScope.Solution))
                return null;
            if (id is "PORTIA200" or "PORTIA201" && !SupportsFileChanges(context.Solution.Workspace))
                return null;
            var documents = context.Scope switch
            {
                FixAllScope.Document when context.Document is { } document => new[] { document },
                FixAllScope.Project => context.Project.Documents.ToArray(),
                FixAllScope.Solution => context.Solution.Projects.SelectMany(project => project.Documents).ToArray(),
                _ => []
            };
            var changes = new Dictionary<DocumentId, ImmutableArray<Diagnostic>>();
            foreach (var document in documents)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var diagnostics = await context.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false);
                var selected = diagnostics.Where(diagnostic => diagnostic.Id == id && !diagnostic.IsSuppressed).ToImmutableArray();
                if (!selected.IsEmpty)
                    changes.Add(document.Id, selected);
            }
            if (changes.Count == 0)
                return null;
            return CodeAction.Create("Fix all application structure violations",
                ct => ApplyAsync(context.Solution, changes, id, ct), context.CodeActionEquivalenceKey);
        }

        static async Task<Solution> ApplyAsync(Solution solution,
            Dictionary<DocumentId, ImmutableArray<Diagnostic>> changes, string id, CancellationToken ct)
        {
            foreach (var pair in changes)
            {
                ct.ThrowIfCancellationRequested();
                var document = solution.GetDocument(pair.Key);
                if (document is null || await document.GetSyntaxRootAsync(ct).ConfigureAwait(false) is not CompilationUnitSyntax root)
                    continue;
                if (id == "PORTIA200")
                {
                    solution = await SplitAsync(document, root, pair.Value, ct).ConfigureAwait(false);
                    continue;
                }
                if (id == "PORTIA201")
                {
                    if (pair.Value[0].Properties.TryGetValue("ExpectedFileName", out var name) && name is not null
                        && AvailableFile(document, name))
                        solution = await RenameFileAsync(document, name, ct).ConfigureAwait(false);
                    continue;
                }

                // The namespace refactoring updates references across projects. Apply one document
                // at a time to the evolving solution so those edits are retained.
                var original = pair.Value[0];
                var oldRoot = await original.Location.SourceTree!.GetRootAsync(ct).ConfigureAwait(false);
                var oldDeclaration = oldRoot.FindNode(original.Location.SourceSpan).AncestorsAndSelf()
                    .OfType<MemberDeclarationSyntax>().FirstOrDefault(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
                var identifier = oldDeclaration switch
                {
                    BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                    DelegateDeclarationSyntax declaration => declaration.Identifier.ValueText,
                    _ => null
                };
                var current = root.DescendantNodes().OfType<MemberDeclarationSyntax>().FirstOrDefault(node => node switch
                {
                    BaseTypeDeclarationSyntax type => type.Identifier.ValueText == identifier,
                    DelegateDeclarationSyntax declaration => declaration.Identifier.ValueText == identifier,
                    _ => false
                });
                if (current is null)
                    continue;
                var token = current is BaseTypeDeclarationSyntax typeDeclaration ? typeDeclaration.Identifier
                    : ((DelegateDeclarationSyntax)current).Identifier;
                var diagnostic = Diagnostic.Create(original.Descriptor, token.GetLocation(), original.Properties,
                    identifier, original.Properties["ExpectedNamespace"]);
                var actions = new List<CodeAction>();
                var codeContext = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), ct);
                await new ApplicationStructureCodeFixProvider().RegisterCodeFixesAsync(codeContext).ConfigureAwait(false);
                if (actions.FirstOrDefault() is { } action)
                {
                    var operations = await action.GetOperationsAsync(ct).ConfigureAwait(false);
                    solution = operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution ?? solution;
                }
            }
            return solution;
        }

        static async Task<Solution> SplitAsync(Document document, CompilationUnitSyntax root,
            ImmutableArray<Diagnostic> diagnostics, CancellationToken ct)
        {
            if (!SafeToSplit(root))
                return document.Project.Solution;
            var moves = diagnostics.Select(diagnostic =>
            {
                _ = diagnostic.Properties.TryGetValue("ExpectedFileName", out var name);
                var node = root.FindNode(diagnostic.Location.SourceSpan).AncestorsAndSelf()
                    .OfType<MemberDeclarationSyntax>().FirstOrDefault(item => item is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
                return (Name: name, Node: node);
            }).Where(move => move.Name is not null && move.Node is not null
                && move.Node.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax
                && !move.Node.Modifiers.Any(SyntaxKind.PartialKeyword)
                && !move.Node.GetLeadingTrivia().Any(trivia => trivia.IsDirective)).ToArray();
            var eligible = moves.Where(move => moves.Count(other => string.Equals(other.Name, move.Name, StringComparison.OrdinalIgnoreCase)) == 1
                && AvailableFile(document, move.Name!) && !string.Equals(document.Name, move.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (eligible.Length == 0)
                return document.Project.Solution;
            var text = await document.GetTextAsync(ct).ConfigureAwait(false);
            var remaining = root.RemoveNodes(eligible.Select(move => move.Node!), SyntaxRemoveOptions.KeepNoTrivia)!;
            var solution = document.Project.Solution.WithDocumentSyntaxRoot(document.Id, remaining);
            foreach (var move in eligible)
            {
                ct.ThrowIfCancellationRequested();
                var moved = MovedRoot(root, move.Node!);
                var newId = DocumentId.CreateNewId(document.Project.Id);
                solution = solution.AddDocument(newId, move.Name!, SourceText.From(moved.ToFullString(), text.Encoding),
                    document.Folders, SiblingPath(document.FilePath, move.Name!));
                solution = (await Formatter.FormatAsync(solution.GetDocument(newId)!, Formatter.Annotation, cancellationToken: ct)
                    .ConfigureAwait(false)).Project.Solution;
            }
            return solution;
        }
    }

    static bool AvailableFile(Document document, string filename)
    {
        if (document.FilePath is { } path && document.Project.Solution.GetDocumentIdsWithFilePath(path).Length > 1)
            return false;
        var destination = SiblingPath(document.FilePath, filename);
#pragma warning disable RS1035 // Code actions preflight files outside the solution before offering a file operation.
        if (destination is not null && System.IO.File.Exists(destination)
            && !string.Equals(System.IO.Path.GetFullPath(destination),
                System.IO.Path.GetFullPath(document.FilePath ?? string.Empty), StringComparison.Ordinal))
#pragma warning restore RS1035
            return false;
        return !document.Project.Solution.Projects.SelectMany(project => project.Documents).Any(other => other.Id != document.Id
            && (destination is not null && string.Equals(other.FilePath, destination, StringComparison.OrdinalIgnoreCase)
                || destination is null && other.Project.Id == document.Project.Id
                && string.Equals(other.Name, filename, StringComparison.OrdinalIgnoreCase)
                && other.Folders.SequenceEqual(document.Folders)));
    }

    static string? SiblingPath(string? path, string filename)
    {
        var slash = path?.LastIndexOfAny(['/', '\\']) ?? -1;
        return slash >= 0 ? path!.Substring(0, slash + 1) + filename : null;
    }

    static async Task<Solution> RenameFileAsync(Document document, string filename, CancellationToken ct)
    {
        if (!document.Project.Solution.Workspace.CanApplyChange(ApplyChangesKind.ChangeDocumentInfo))
        {
            var text = await document.GetTextAsync(ct).ConfigureAwait(false);
            var id = DocumentId.CreateNewId(document.Project.Id);
            return document.Project.Solution.RemoveDocument(document.Id)
                .AddDocument(id, filename, text, document.Folders, SiblingPath(document.FilePath, filename));
        }
        var solution = document.Project.Solution.WithDocumentName(document.Id, filename);
        return SiblingPath(document.FilePath, filename) is { } path
            ? solution.WithDocumentFilePath(document.Id, path) : solution;
    }

    static async Task<Solution> MoveTypeAsync(Document document, CompilationUnitSyntax root,
        MemberDeclarationSyntax declaration, string filename, CancellationToken ct)
    {
        var movedRoot = MovedRoot(root, declaration);
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
                && (symbol.DeclaringSyntaxReferences.Length > 1
                    || model.Compilation.GetTypeByMetadataName(target + "." + symbol.MetadataName) is { } existing
                    && !SymbolEqualityComparer.Default.Equals(symbol, existing)))
            {
                return;
            }
        }

        context.RegisterCodeFix(CodeAction.Create($"Change namespace to '{target}' and update references",
            ct => SyncNamespaceAsync(context.Document, target, folders, ct), "Portia.SyncApplicationNamespace"), diagnostic);
    }

    static async Task<Solution> SyncNamespaceAsync(Document document, string target, string[] folders, CancellationToken ct)
    {
        if (await document.GetSyntaxRootAsync(ct).ConfigureAwait(false) is not CompilationUnitSyntax root
            || await document.GetSemanticModelAsync(ct).ConfigureAwait(false) is not { } model)
            return document.Project.Solution;
        // Use Roslyn's document namespace refactoring so references across the solution are updated.
        // A temporary logical folder makes the refactoring run even when the physical folders are correct.
        var prepared = await PrepareConstructorReferencesAsync(document, root, model!, ct).ConfigureAwait(false);
        prepared = prepared.WithFolders(prepared.Folders.Concat(["PortiaNamespaceFix"]));
        var actions = await Renamer.RenameDocumentAsync(prepared, new DocumentRenameOptions(), prepared.Name,
            folders, ct).ConfigureAwait(false);
        if (actions.ApplicableActions.Length == 0 || actions.ApplicableActions.Any(action => action.GetErrors(CultureInfo.InvariantCulture).Any()))
            return document.Project.Solution;
        Solution corrected;
        try
        {
            corrected = await actions.UpdateSolutionAsync(prepared.Project.Solution, ct).ConfigureAwait(false);
            // Roslyn's namespace refactoring excludes constructor definitions from its type
            // reference results. Repair the qualified constructor references captured beforehand.
            foreach (var id in corrected.GetChanges(document.Project.Solution).GetProjectChanges()
                         .SelectMany(change => change.GetChangedDocuments()))
            {
                var referenceRoot = await corrected.GetDocument(id)!.GetSyntaxRootAsync(ct).ConfigureAwait(false);
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
            return document.Project.Solution;
        }

        var changed = corrected.GetDocument(document.Id)!;
        var changedRoot = await changed.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var changedModel = await changed.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (changedRoot is null || changedModel is null || changedRoot.DescendantNodes()
            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Any(node => changedModel.GetDeclaredSymbol(node, ct)?.ContainingNamespace.ToDisplayString() != target))
        {
            return document.Project.Solution;
        }

        // RenameDocument uses temporary logical folders. Copy the resulting text into the
        // original solution so hosts that cannot change document properties can apply it.
        var result = document.Project.Solution;
        foreach (var id in corrected.GetChanges(result).GetProjectChanges().SelectMany(change => change.GetChangedDocuments()))
            result = result.WithDocumentText(id, await corrected.GetDocument(id)!.GetTextAsync(ct).ConfigureAwait(false));
        return result;
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
                    // Source-generated references are regenerated from their user-authored
                    // inputs after the move; they are not editable solution documents.
                    if (document.Project.Solution.GetDocument(location.Document.Id) is null)
                        continue;
                    if (!locations.TryGetValue(location.Document.Id, out var spans))
                        locations.Add(location.Document.Id, spans = []);
                    _ = spans.Add(location.Location.SourceSpan);
                }
            }
        }

        var solution = document.Project.Solution;
        foreach (var pair in locations)
        {
            if (solution.GetDocument(pair.Key) is not { } referenceDocument
                || await referenceDocument.GetSyntaxRootAsync(ct).ConfigureAwait(false) is not { } referenceRoot)
                continue;
            var types = pair.Value.Select(span => referenceRoot.FindNode(span).FirstAncestorOrSelf<ObjectCreationExpressionSyntax>()?.Type)
                .Where(type => type is QualifiedNameSyntax or AliasQualifiedNameSyntax).Distinct().Cast<TypeSyntax>().ToArray();
            solution = solution.WithDocumentSyntaxRoot(pair.Key, referenceRoot.ReplaceNodes(types,
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

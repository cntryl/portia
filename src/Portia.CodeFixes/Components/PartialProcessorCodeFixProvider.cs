using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>
///     Adds the partial modifier required for generated projector and reactor dispatch, to the processor or to the
///     types that enclose it.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PartialProcessorCodeFixProvider))]
[Shared]
public sealed class PartialProcessorCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["PORTIA002", "PORTIA005", "PORTIA015"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var declaration = root?.FindNode(context.Span).FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (declaration is null)
            return;

        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.Id == "PORTIA015")
            {
                // PORTIA015 has several causes; only the one the generator marks is repaired here.
                if (!diagnostic.Properties.ContainsKey("PartialContainers"))
                    continue;
                var containers = declaration.Ancestors().OfType<TypeDeclarationSyntax>()
                    .Where(container => !container.Modifiers.Any(SyntaxKind.PartialKeyword)).ToArray();
                if (containers.Length == 0)
                    continue;
                context.RegisterCodeFix(CodeAction.Create("Make enclosing types partial",
                    ct => AddPartialAsync(context.Document, containers, ct), "Portia.MakeContainersPartial"),
                    diagnostic);
            }
            else if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                context.RegisterCodeFix(CodeAction.Create("Make processor partial",
                    ct => AddPartialAsync(context.Document, [declaration], ct), "Portia.MakeProcessorPartial"),
                    diagnostic);
            }
        }
    }

    static async Task<Document> AddPartialAsync(Document document, IReadOnlyCollection<TypeDeclarationSyntax> declarations,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return document.WithSyntaxRoot(root!.ReplaceNodes(declarations, (_, current) => WithPartial(current)));
    }

    // Appended last, so it sits immediately before the type keyword as C# requires.
    static TypeDeclarationSyntax WithPartial(TypeDeclarationSyntax declaration)
    {
        var partial = SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        var replacement = declaration;
        if (declaration.Modifiers.Count == 0)
        {
            partial = partial.WithLeadingTrivia(declaration.Keyword.LeadingTrivia);
            replacement = declaration.ReplaceToken(declaration.Keyword,
                declaration.Keyword.WithLeadingTrivia(default(SyntaxTriviaList)));
        }

        return replacement.WithModifiers(replacement.Modifiers.Add(partial));
    }
}

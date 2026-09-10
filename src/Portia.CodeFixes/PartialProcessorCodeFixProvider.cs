using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Adds the partial modifier required for generated projector and reactor dispatch.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PartialProcessorCodeFixProvider))]
[Shared]
public sealed class PartialProcessorCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["PORTIA002", "PORTIA005"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var declaration = root?.FindNode(context.Span).FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (declaration is null || declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            return;

        context.RegisterCodeFix(CodeAction.Create("Make processor partial",
            ct => AddPartialAsync(context.Document, declaration, ct), "Portia.MakeProcessorPartial"),
            context.Diagnostics);
    }

    static async Task<Document> AddPartialAsync(Document document, ClassDeclarationSyntax declaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var partial = SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        return document.WithSyntaxRoot(root!.ReplaceNode(declaration,
            declaration.WithModifiers(declaration.Modifiers.Add(partial))));
    }
}

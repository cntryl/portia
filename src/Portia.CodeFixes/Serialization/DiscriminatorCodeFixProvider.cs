using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>
///     Declares the discriminator a transported request or domain event is missing, or repairs the one it
///     declares with an empty name or a non-positive version. The inserted name is the type's own name; the
///     application still chooses the stable logical name it means to persist.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DiscriminatorCodeFixProvider))]
[Shared]
public sealed class DiscriminatorCodeFixProvider : CodeFixProvider
{
    const string AttributeMetadataName = "Cntryl.Portia.DiscriminatorAttribute";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["PORTIA020", "PORTIA021"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var declaration = root?.FindNode(context.Span).FirstAncestorOrSelf<TypeDeclarationSyntax>();
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || declaration is null || model is null
            || model.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } symbol)
        {
            return;
        }

        // Read through the symbol, so an attribute on another part of a partial type is seen. One declared
        // there cannot be repaired from this part, and adding a second would be a duplicate.
        var declared = symbol.GetAttributes()
            .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == AttributeMetadataName);
        AttributeSyntax? existing = null;
        if (declared is not null)
        {
            if (declared.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken) is not AttributeSyntax
                    attribute || attribute.SyntaxTree != declaration.SyntaxTree
                || !declaration.Span.Contains(attribute.Span))
            {
                return;
            }

            existing = attribute;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (existing is null)
            {
                context.RegisterCodeFix(CodeAction.Create("Declare a discriminator",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(
                        root.ReplaceNode(declaration, WithDiscriminator(root, declaration, model)))),
                    "Portia.AddDiscriminator"), diagnostic);
            }
            else
            {
                context.RegisterCodeFix(CodeAction.Create("Repair the discriminator",
                    ct => Task.FromResult(context.Document.WithSyntaxRoot(
                        Repaired(root, declaration, existing, model, ct))),
                    "Portia.RepairDiscriminator"), diagnostic);
            }
        }
    }

    // The new list goes after the declaration's own attributes, laid out like the last of them. A declaration
    // without any takes the list in front, and the list owns the leading trivia so documentation stays above it.
    static TypeDeclarationSyntax WithDiscriminator(SyntaxNode root, TypeDeclarationSyntax declaration,
        SemanticModel model)
    {
        var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(
            AttributeName(declaration, model),
            SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.AttributeArgument(NameLiteral(declaration)))))));
        if (declaration.AttributeLists.Count > 0)
        {
            var last = declaration.AttributeLists[declaration.AttributeLists.Count - 1];
            var trailing = last.GetTrailingTrivia();
            var leading = trailing.Any(SyntaxKind.EndOfLineTrivia)
                ? Indentation(last.GetLeadingTrivia())
                : default;
            return declaration.AddAttributeLists(list.WithLeadingTrivia(leading).WithTrailingTrivia(trailing));
        }

        var declarationLeading = declaration.GetLeadingTrivia();
        list = list.WithLeadingTrivia(declarationLeading)
            .WithTrailingTrivia(Indentation(declarationLeading).Insert(0, EndOfLine(root)));
        return declaration.WithLeadingTrivia(default(SyntaxTriviaList))
            .WithAttributeLists(SyntaxFactory.SingletonList(list));
    }

    // Only the arguments that fail the contract change; a valid name or version is kept as written.
    static SyntaxNode Repaired(SyntaxNode root, TypeDeclarationSyntax declaration, AttributeSyntax existing,
        SemanticModel model, CancellationToken ct)
    {
        if (existing.ArgumentList is not { Arguments.Count: > 0 } arguments)
        {
            var declared = SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.AttributeArgument(NameLiteral(declaration))));
            return root.ReplaceNode(existing, existing.WithArgumentList(declared));
        }

        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        var positional = 0;
        foreach (var argument in arguments.Arguments)
        {
            if (argument.NameEquals is not null)
                continue;
            var parameter = argument.NameColon?.Name.Identifier.ValueText ?? (positional++ == 0 ? "name" : "version");
            var value = model.GetConstantValue(argument.Expression, ct);
            switch (parameter)
            {
                case "name" when value.Value is not string name || string.IsNullOrWhiteSpace(name):
                    replacements[argument.Expression] = NameLiteral(declaration).WithTriviaFrom(argument.Expression);
                    break;
                case "version" when value.Value is not int version || version <= 0:
                    replacements[argument.Expression] = SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1))
                        .WithTriviaFrom(argument.Expression);
                    break;
            }
        }

        return replacements.Count == 0
            ? root
            : root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
    }

    static LiteralExpressionSyntax NameLiteral(TypeDeclarationSyntax declaration) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(declaration.Identifier.ValueText));

    // The short name is used only where it binds to Portia's attribute and nothing else; otherwise the name
    // is written global-qualified, as the serializer-root fix does.
    static NameSyntax AttributeName(TypeDeclarationSyntax declaration, SemanticModel model)
    {
        var position = declaration.SpanStart;
        var portia = model.LookupNamespacesAndTypes(position, name: "DiscriminatorAttribute")
            .Any(candidate => candidate is INamedTypeSymbol type && type.ToDisplayString() == AttributeMetadataName);
        var shadowed = model.LookupNamespacesAndTypes(position, name: "Discriminator").Length > 0;
        return portia && !shadowed
            ? SyntaxFactory.IdentifierName("Discriminator")
            : SyntaxFactory.ParseName("global::Cntryl.Portia.Discriminator");
    }

    // The whitespace after the last line break: what indents the declaration's first token.
    static SyntaxTriviaList Indentation(SyntaxTriviaList leading)
    {
        var indentation = SyntaxTriviaList.Empty;
        foreach (var trivia in leading)
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                indentation = SyntaxTriviaList.Empty;
            else if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                indentation = indentation.Add(trivia);
            else
                indentation = SyntaxTriviaList.Empty;
        }

        return indentation;
    }

    static SyntaxTrivia EndOfLine(SyntaxNode root)
    {
        foreach (var trivia in root.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                return SyntaxFactory.EndOfLine(trivia.ToString());
        }

        return SyntaxFactory.LineFeed;
    }
}

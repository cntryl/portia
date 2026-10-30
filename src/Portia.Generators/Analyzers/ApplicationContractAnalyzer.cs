using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia;

/// <summary>Checks data-only Common contracts and explicit feature discriminators.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ApplicationContractAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Behavior = new("PORTIA209", "Common declares executable behavior",
        "Common member '{0}' declares executable behavior; keep business behavior in Core", "Portia", DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor Discriminator = new("PORTIA210", "Contract requires an explicit feature discriminator",
        "Contract '{0}' needs [Discriminator(\"{1}.name\", version)] with two snake_case parts and an explicit positive schema version",
        "Portia", DiagnosticSeverity.Warning, true);

    static readonly Regex WireName = new("\\A[a-z][a-z0-9]*(?:_[a-z0-9]+)*\\.[a-z][a-z0-9]*(?:_[a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Behavior, Discriminator];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (ApplicationProject.From(start.Options.AnalyzerConfigOptionsProvider) is not { Role: "Common", IsTest: false } project)
                return;
            start.RegisterSyntaxNodeAction(AnalyzeBehavior, SyntaxKind.MethodDeclaration, SyntaxKind.ConstructorDeclaration,
                SyntaxKind.DestructorDeclaration, SyntaxKind.OperatorDeclaration, SyntaxKind.ConversionOperatorDeclaration,
                SyntaxKind.PropertyDeclaration, SyntaxKind.IndexerDeclaration, SyntaxKind.EventDeclaration,
                SyntaxKind.FieldDeclaration, SyntaxKind.EventFieldDeclaration);
            start.RegisterSymbolAction(symbol => AnalyzeDiscriminator(symbol, project), SymbolKind.NamedType);
        });
    }

    static void AnalyzeBehavior(SyntaxNodeAnalysisContext context)
    {
        var executable = context.Node switch
        {
            BaseMethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
            PropertyDeclarationSyntax property => property.ExpressionBody is not null || HasAccessorBody(property.AccessorList)
                || CallsCode(property.Initializer),
            IndexerDeclarationSyntax indexer => indexer.ExpressionBody is not null || HasAccessorBody(indexer.AccessorList),
            EventDeclarationSyntax @event => HasAccessorBody(@event.AccessorList),
            BaseFieldDeclarationSyntax field => field.Declaration.Variables.Any(variable => CallsCode(variable.Initializer)),
            _ => false
        };
        if (!executable)
            return;
        var name = context.Node switch
        {
            MethodDeclarationSyntax method => method.Identifier,
            ConstructorDeclarationSyntax constructor => constructor.Identifier,
            DestructorDeclarationSyntax destructor => destructor.Identifier,
            OperatorDeclarationSyntax @operator => @operator.OperatorToken,
            ConversionOperatorDeclarationSyntax conversion => conversion.ImplicitOrExplicitKeyword,
            PropertyDeclarationSyntax property => property.Identifier,
            IndexerDeclarationSyntax indexer => indexer.ThisKeyword,
            EventDeclarationSyntax @event => @event.Identifier,
            BaseFieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier,
            _ => default
        };
        context.ReportDiagnostic(Diagnostic.Create(Behavior, name.GetLocation(), name.ValueText));

        static bool HasAccessorBody(AccessorListSyntax? accessors) =>
            accessors?.Accessors.Any(accessor => accessor.Body is not null || accessor.ExpressionBody is not null) == true;

        static bool CallsCode(EqualsValueClauseSyntax? initializer) => initializer?.DescendantNodes().Any(node =>
            node is InvocationExpressionSyntax or AnonymousFunctionExpressionSyntax or AwaitExpressionSyntax) == true;
    }

    static void AnalyzeDiscriminator(SymbolAnalysisContext context, ApplicationProject project)
    {
        var symbol = (INamedTypeSymbol)context.Symbol;
        if (!ApplicationProject.IsRequest(symbol) && !ApplicationProject.DerivesFrom(symbol, "DomainEvent"))
            return;
        var declaration = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null)
            return;
        var path = project.RelativePath(declaration.SyntaxTree);
        if (path.Length < 3 || path[0] != "Features")
            return;
        var feature = SnakeCase(path[1]);
        var attribute = symbol.GetAttributes().FirstOrDefault(item => item.AttributeClass is { } type
            && SymbolNames.Is(type, "Cntryl.Portia.DiscriminatorAttribute"));
        var syntax = attribute?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken) as AttributeSyntax;
        var arguments = syntax?.ArgumentList?.Arguments;
        var explicitVersion = arguments is { } list && (list.Any(argument => argument.NameColon?.Name.Identifier.ValueText == "version")
            || list.Count > 1 && list[1].NameColon is null && list[1].NameEquals is null);
        var constructorArguments = attribute?.ConstructorArguments ?? [];
        var name = constructorArguments.Length > 0 ? constructorArguments[0].Value as string : null;
        var version = constructorArguments.Length > 1 && constructorArguments[1].Value is int value ? value : 0;
        var identityValid = version > 0 && name is not null
            && name.StartsWith(feature + ".", StringComparison.Ordinal) && WireName.IsMatch(name);
        if (!identityValid || !explicitVersion)
        {
            var properties = identityValid && !explicitVersion
                ? ImmutableDictionary<string, string?>.Empty.Add("ImplicitVersion", version.ToString(CultureInfo.InvariantCulture))
                : ImmutableDictionary<string, string?>.Empty;
            context.ReportDiagnostic(Diagnostic.Create(Discriminator, syntax?.GetLocation() ?? symbol.Locations[0],
                properties, symbol.Name, feature));
        }
    }

    static string SnakeCase(string feature)
    {
        var result = new StringBuilder(feature.Length + 4);
        for (var index = 0; index < feature.Length; index++)
        {
            var current = feature[index];
            if (char.IsUpper(current) && index > 0 && feature[index - 1] != '_'
                && (char.IsLower(feature[index - 1]) || char.IsDigit(feature[index - 1])
                    || index + 1 < feature.Length && char.IsLower(feature[index + 1])))
            {
                _ = result.Append('_');
            }

            _ = result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }
}

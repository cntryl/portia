using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Discovers the Portia JSON serializer contexts declared in, or exported to, a compilation.</summary>
static class JsonContextDiscovery
{
    public static IncrementalValueProvider<ImmutableArray<DiscoveredJsonContext>> DeclaredContexts(
        IncrementalGeneratorInitializationContext context) =>
        context.SyntaxProvider.ForAttributeWithMetadataName("Cntryl.Portia.PortiaJsonContextAttribute",
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => JsonContextModel((INamedTypeSymbol)ctx.TargetSymbol))
            .Collect();

    public static IncrementalValueProvider<ImmutableArray<DiscoveredJsonContext>> ReferencedContexts(
        IncrementalGeneratorInitializationContext context) =>
        context.CompilationProvider.Select(static (compilation, _) => ReferencedJsonContexts(compilation));

    static DiscoveredJsonContext JsonContextModel(INamedTypeSymbol symbol) =>
        new(symbol.ToDisplayString(), Type(symbol));

    static ImmutableArray<DiscoveredJsonContext> ReferencedJsonContexts(Compilation compilation)
    {
        var contexts = ImmutableArray.CreateBuilder<DiscoveredJsonContext>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            foreach (var attribute in assembly.GetAttributes().Where(candidate =>
                         candidate.AttributeClass?.ToDisplayString() == "Cntryl.Portia.PortiaJsonRootAttribute"))
            {
                if (attribute.ConstructorArguments.Length > 2
                    && attribute.ConstructorArguments[2].Value is INamedTypeSymbol factory)
                {
                    contexts.Add(new DiscoveredJsonContext(factory.ToDisplayString(), Type(factory) + ".Create"));
                }
            }
        }

        return contexts.ToImmutable();
    }

    static string Type(ITypeSymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}

sealed record DiscoveredJsonContext(string DisplayName, string TypeName);

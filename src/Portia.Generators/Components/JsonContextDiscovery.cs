using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Discovers the Portia JSON serializer contexts declared in, or exported to, a compilation.</summary>
static class JsonContextDiscovery
{
    static readonly ConditionalWeakTable<IAssemblySymbol, DiscoveredJsonContext[]> ReferencedByAssembly = new();

    public static IncrementalValueProvider<ImmutableArray<DiscoveredJsonContext>> DeclaredContexts(
        IncrementalGeneratorInitializationContext context) =>
        context.SyntaxProvider.ForAttributeWithMetadataName("Cntryl.Portia.PortiaJsonContextAttribute",
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => JsonContextModel((INamedTypeSymbol)ctx.TargetSymbol))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

    public static IncrementalValueProvider<ImmutableArray<DiscoveredJsonContext>> ReferencedContexts(
        IncrementalGeneratorInitializationContext context) =>
        context.CompilationProvider.Select(static (compilation, _) => ReferencedJsonContexts(compilation));

    static DiscoveredJsonContext? JsonContextModel(INamedTypeSymbol symbol) =>
        JsonContextShape.UnsupportedReason(symbol) is null ? new(symbol.ToDisplayString(), Type(symbol)) : null;

    static ImmutableArray<DiscoveredJsonContext> ReferencedJsonContexts(Compilation compilation)
    {
        var contexts = ImmutableArray.CreateBuilder<DiscoveredJsonContext>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            contexts.AddRange(ReferencedByAssembly.GetValue(assembly, AssemblyContexts));
        return contexts.ToImmutable();
    }

    static DiscoveredJsonContext[] AssemblyContexts(IAssemblySymbol assembly)
    {
        var contexts = new List<DiscoveredJsonContext>();
        var factories = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var attribute in assembly.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeType
                && SymbolNames.Is(attributeType, "Cntryl.Portia.PortiaJsonRootAttribute")
                && attribute.ConstructorArguments.Length > 2
                && attribute.ConstructorArguments[2].Value is INamedTypeSymbol factory
                && factories.Add(factory))
            {
                contexts.Add(new DiscoveredJsonContext(factory.ToDisplayString(), Type(factory) + ".Create"));
            }
        }

        return [.. contexts];
    }

    static string Type(ITypeSymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}

sealed record DiscoveredJsonContext(string DisplayName, string TypeName);

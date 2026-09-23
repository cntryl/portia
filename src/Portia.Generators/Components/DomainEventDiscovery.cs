using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Discovers the domain events an application registers: declared in it, or referenced from it.</summary>
static class DomainEventDiscovery
{
    public static IncrementalValueProvider<ImmutableArray<DiscoveredEvent>> DeclaredEvents(
        IncrementalGeneratorInitializationContext context) =>
        context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, _) => DeclaredEventModel(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

    public static IncrementalValueProvider<ImmutableArray<DiscoveredEvent>> ReferencedEvents(
        IncrementalGeneratorInitializationContext context) =>
        context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeSyntax,
                static (ctx, _) => ReferencedEventModel(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

    static DiscoveredEvent? DeclaredEventModel(GeneratorSyntaxContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        return context.SemanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol
               && IsRegistrableEvent(symbol, true)
            ? EventModel(symbol)
            : null;
    }

    // Type syntax covers the semantic edges that make an external event part of this application:
    // handler interfaces, aggregate On<TEvent> calls, method signatures, construction, casts, and
    // explicit generic dispatch. A project reference by itself is deliberately not such an edge.
    static DiscoveredEvent? ReferencedEventModel(GeneratorSyntaxContext context)
    {
        return context.SemanticModel.GetTypeInfo((TypeSyntax)context.Node).Type is INamedTypeSymbol type
               && !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly,
                   context.SemanticModel.Compilation.Assembly)
               && IsRegistrableEvent(type, false)
            ? EventModel(type)
            : null;
    }

    // One discriminator cannot describe every constructed form of a generic event. This also
    // keeps open type parameters out of the generated, non-generic AddPortia interceptor.
    static bool IsRegistrableEvent(INamedTypeSymbol symbol, bool requireSameAssembly) =>
        !symbol.IsGenericType && IsDomainEvent(symbol, requireSameAssembly);

    static DiscoveredEvent? EventModel(INamedTypeSymbol symbol)
    {
        var attribute = symbol.GetAttributes().FirstOrDefault(candidate =>
            candidate.AttributeClass?.ToDisplayString() == "Cntryl.Portia.DiscriminatorAttribute");
        return attribute is null || attribute.ConstructorArguments.Length != 2
            ? null
            : new DiscoveredEvent(symbol.ToDisplayString(),
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                attribute.ConstructorArguments[0].Value as string ?? string.Empty,
                attribute.ConstructorArguments[1].Value as int? ?? 0);
    }

    static bool IsDomainEvent(INamedTypeSymbol symbol, bool currentAssembly)
    {
        if (symbol.IsAbstract || (!currentAssembly && symbol.DeclaredAccessibility != Accessibility.Public)
                              || GeneratedTypeShape.InaccessibleReason(symbol) is not null)
        {
            return false;
        }

        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "Cntryl.Portia.DomainEvent")
                return true;
        }

        return false;
    }
}

sealed record DiscoveredEvent(string DisplayName, string TypeName, string Name, int Version);

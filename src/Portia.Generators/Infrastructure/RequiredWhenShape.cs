using Microsoft.CodeAnalysis;

namespace Cntryl.Portia;

static class RequiredWhenShape
{
    internal const string AttributeName = "Cntryl.Portia.RequiredWhenAttribute";

    internal static IEnumerable<AttributeData> Attributes(ISymbol member)
    {
        var attributes = member.GetAttributes().AsEnumerable();
        if (member is IPropertySymbol property &&
            Constructor(property.ContainingType)?.Parameters.FirstOrDefault(parameter =>
                Matches(parameter, property)) is { } associated)
            attributes = attributes.Concat(associated.GetAttributes());
        return attributes.Where(attribute => attribute.AttributeClass?.ToDisplayString() == AttributeName);
    }

    internal sealed record Conditional(string Selector, string Value);

    internal static Conditional? Resolve(INamedTypeSymbol type, ISymbol member, AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length != 2 ||
            attribute.ConstructorArguments[0].Value is not string selectorName ||
            attribute.ConstructorArguments[1].Value is not string value)
            return null;

        var binding = Constructor(type);
        if (binding is null || member.Name == selectorName ||
            type.GetMembers(selectorName).OfType<IPropertySymbol>().FirstOrDefault() is not { } selector ||
            member is not IPropertySymbol target || !Readable(target) || !Readable(selector))
            return null;
        var targetParameter = Parameter(target);
        var selectorParameter = Parameter(selector);
        if (targetParameter is null || targetParameter is { HasExplicitDefaultValue: true, ExplicitDefaultValue: not null } ||
            targetParameter.Type.NullableAnnotation != NullableAnnotation.Annotated &&
            targetParameter.Type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } ||
            selectorParameter is not { Type.SpecialType: SpecialType.System_String, HasExplicitDefaultValue: false })
            return null;
        return new Conditional(selector.Name, value);

        IParameterSymbol? Parameter(IPropertySymbol property) => binding.Parameters.FirstOrDefault(parameter =>
            Matches(parameter, property));

        static bool Readable(IPropertySymbol property) => !property.IsStatic && !property.IsIndexer &&
            property.GetMethod?.DeclaredAccessibility == Accessibility.Public;
    }

    static bool Matches(IParameterSymbol parameter, IPropertySymbol property) =>
        string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase) &&
        SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type);

    static IMethodSymbol? Constructor(INamedTypeSymbol type)
    {
        var constructors = type.InstanceConstructors.Where(ctor => ctor.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        return constructors.FirstOrDefault(ctor => ctor.GetAttributes().Any(candidate =>
                          candidate.AttributeClass?.ToDisplayString() ==
                          "System.Text.Json.Serialization.JsonConstructorAttribute"))
                      ?? (constructors.Length == 1 ? constructors[0] : null);
    }
}

using System.Text;
using Microsoft.CodeAnalysis;

namespace Cntryl.Portia;

// Read standard attributes at compile time, including metadata from referenced contracts.
// The emitted resolver contains only type comparisons and string literals.
static class JsonDescriptionEmission
{
    internal static string? Description(ISymbol symbol)
    {
        var attributes = symbol.GetAttributes();
        var description = attributes.FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;
        if (!string.IsNullOrWhiteSpace(description))
            return description;
        var display = attributes.FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.DisplayAttribute");
        // Resource-backed display descriptions require application-owned localization.
        if (display?.NamedArguments.Any(argument => argument.Key == "ResourceType" && !argument.Value.IsNull) == true)
            return null;
        description = display?.NamedArguments.FirstOrDefault(argument => argument.Key == "Description").Value.Value as string;
        if (!string.IsNullOrWhiteSpace(description))
            return description;
        if (symbol is IPropertySymbol property)
        {
            if (property.OverriddenProperty is { } overridden && Description(overridden) is { } inherited)
                return inherited;
            foreach (var constructor in property.ContainingType.InstanceConstructors)
            {
                var parameter = constructor.Parameters.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase) &&
                    SymbolEqualityComparer.Default.Equals(candidate.Type, property.Type));
                if (parameter is not null && Description(parameter) is { } parameterText)
                    return parameterText;
            }
        }
        return symbol is INamedTypeSymbol { BaseType: { } baseType } ? Description(baseType) : null;
    }

    internal static string Body(IEnumerable<ITypeSymbol> roots)
    {
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<ITypeSymbol>(roots);
        var blocks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            if (!seen.Add(type))
                continue;
            if (type is IArrayTypeSymbol array)
            {
                pending.Enqueue(array.ElementType);
                continue;
            }
            if (type is not INamedTypeSymbol named || named.IsUnboundGenericType ||
                named.TypeKind is TypeKind.Delegate or TypeKind.Error)
                continue;
            foreach (var argument in named.TypeArguments)
                pending.Enqueue(argument);
            if (named.SpecialType != SpecialType.None || named.ContainingNamespace.ToDisplayString() is "System" ||
                named.ContainingNamespace.ToDisplayString().StartsWith("System.", StringComparison.Ordinal))
                continue;
            if (named.BaseType is { } baseType)
                pending.Enqueue(baseType);
            foreach (var attribute in named.GetAttributes())
                if (attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonDerivedTypeAttribute" &&
                    attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol derived)
                    pending.Enqueue(derived);
            var members = new List<ISymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var current = named; current is not null; current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public || !names.Add(member.Name))
                        continue;
                    if (member is IPropertySymbol { IsIndexer: false } property)
                    {
                        pending.Enqueue(property.Type);
                        members.Add(property);
                    }
                    else if (member is IFieldSymbol field)
                    {
                        pending.Enqueue(field.Type);
                        members.Add(field);
                    }
                }
            }
            var body = new StringBuilder();
            if (Description(named) is { } text)
                body.Append("if (member is null) return ").Append(Literal(text)).AppendLine(";");
            foreach (var member in members.OrderBy(member => member.Name, StringComparer.Ordinal))
            {
                if (Description(member) is not { } memberText)
                    continue;
                var jsonName = member.GetAttributes().FirstOrDefault(attribute =>
                        attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute")
                    ?.ConstructorArguments.FirstOrDefault().Value as string;
                body.Append("if (global::System.String.Equals(member, ").Append(Literal(member.Name))
                    .Append(", global::System.StringComparison.OrdinalIgnoreCase) || (policy is not null && member == policy.ConvertName(")
                    .Append(Literal(member.Name)).Append("))");
                if (jsonName is not null)
                    body.Append(" || member == ").Append(Literal(jsonName));
                body.Append(") return ").Append(Literal(memberText)).AppendLine(";");
            }
            if (body.Length > 0)
            {
                var name = named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                blocks[name] = "if (type == typeof(" + name + ")) { " + body + " }\n";
            }
        }
        return string.Concat(blocks.Values) + "return null;";
    }

    static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, true);
}

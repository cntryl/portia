using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
        var nodes = Nodes(roots);
        // Only getters on paths to validation are needed. Editorial metadata must not cause
        // arbitrary getters to execute during dispatch, or inflate generated code for plain DTOs.
        var validated = new HashSet<ITypeSymbol>(nodes.Values.Where(node => node.Members.Any(member => ValidationRules(member).Any()))
            .Select(node => node.Type), SymbolEqualityComparer.Default);
        bool changed;
        do
        {
            changed = false;
            foreach (var node in nodes.Values)
                if (node.Children.Any(child => validated.Contains(child.WithNullableAnnotation(NullableAnnotation.NotAnnotated))))
                    changed |= validated.Add(node.Type);
        } while (changed);
        var blocks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            var type = node.Type;
            if (type is not (INamedTypeSymbol or IArrayTypeSymbol))
                continue;
            var unsupported = node.Members.FirstOrDefault(member => Attributes(member).Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() is "System.ComponentModel.DataAnnotations.RangeAttribute" or "System.ComponentModel.DefaultValueAttribute" &&
                attribute.ConstructorArguments.FirstOrDefault().Kind == TypedConstantKind.Type));
            if (unsupported is not null)
            {
                var identity = Type(type);
                blocks[identity] = "if (type == typeof(" + identity + ")) { throw new global::System.InvalidOperationException(" +
                    Literal("Type-converter attribute on '" + type.ToDisplayString() + "." + unsupported.Name + "' is unsupported. Use numeric Range bounds or literal DefaultValue.") + "); }\n";
                continue;
            }
            var facts = new StringBuilder();
            AppendFacts(facts, type);
            if (validated.Contains(type))
                facts.AppendLine("HasValidation = true,");
            if (node.Elements is not null && validated.Contains(type))
                facts.Append("Elements = static value => ").Append(node.Elements).AppendLine(",")
                    .Append("ElementType = typeof(").Append(Type(node.Children[0])).AppendLine("),");
            var members = new StringBuilder();
            foreach (var member in node.Members.OrderBy(member => member.Name, StringComparer.Ordinal))
            {
                var memberType = member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;
                if (memberType.IsRefLikeType || memberType.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
                    continue;
                var memberFacts = new StringBuilder();
                AppendFacts(memberFacts, member);
                var rules = ValidationRules(member).ToArray();
                if (rules.Length > 0)
                    memberFacts.Append("Rules = new global::System.ComponentModel.DataAnnotations.ValidationAttribute[] { ")
                        .Append(string.Join(", ", rules)).AppendLine(" },");
                if (rules.Length > 0 && CountExpression(memberType) is { } count)
                    memberFacts.Append("Count = static value => ").Append(count).AppendLine(",");
                var validates = rules.Length > 0 || validated.Contains(memberType.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
                if (validates)
                    memberFacts.AppendLine("HasValidation = true,");
                if (validates &&
                    (member is IFieldSymbol || member is IPropertySymbol { GetMethod.DeclaredAccessibility: Accessibility.Public }))
                    memberFacts.Append("GetValue = static value => ((").Append(Type(type)).Append(")value).@")
                        .Append(member.Name).AppendLine(",");
                AppendBindingFacts(memberFacts, member, memberType, (INamedTypeSymbol)type);
                if (memberFacts.Length == 0)
                    continue;
                var jsonName = Attributes(member).FirstOrDefault(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute")
                    ?.ConstructorArguments.FirstOrDefault().Value as string;
                if (jsonName is not null)
                    memberFacts.Append("JsonName = ").Append(Literal(jsonName)).AppendLine(",");
                members.Append("new(").Append(Literal(member.Name)).Append(", typeof(").Append(Type(memberType))
                    .AppendLine(")) {").Append(memberFacts).AppendLine("},");
            }
            if (members.Length > 0)
                facts.AppendLine("Members = new global::Cntryl.Portia.PortiaContractMemberMetadata[] {").Append(members).AppendLine("},");
            if (facts.Length == 0)
                continue;
            if (type.Locations.Any(location => location.IsInSource))
                facts.AppendLine("IsSourceDefinition = true,");
            var name = Type(type);
            blocks[name] = "if (type == typeof(" + name + ")) { return new global::Cntryl.Portia.PortiaContractMetadata(typeof(" + name + ")) { " + facts + " }; }\n";
        }
        return string.Concat(blocks.Values) + "return null;";
    }

    internal static string[] ContractTypes(IEnumerable<ITypeSymbol> roots) => Nodes(roots).Keys
        .OfType<INamedTypeSymbol>().Select(type => Type(type.OriginalDefinition)).Distinct(StringComparer.Ordinal).ToArray();

    static Dictionary<ITypeSymbol, Node> Nodes(IEnumerable<ITypeSymbol> roots)
    {
        var nodes = new Dictionary<ITypeSymbol, Node>(SymbolEqualityComparer.Default);
        var pending = new Queue<ITypeSymbol>(roots);
        while (pending.Count > 0)
        {
            var type = pending.Dequeue().WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            if (nodes.ContainsKey(type) || type.IsRefLikeType || type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.TypeParameter)
                continue;
            var node = new Node(type);
            nodes.Add(type, node);
            if (type is IArrayTypeSymbol array)
            {
                node.Children.Add(array.ElementType);
                node.Elements = "global::System.Linq.Enumerable.Cast<object?>((global::System.Collections.IEnumerable)value)";
            }
            else if (type is INamedTypeSymbol named && !named.IsUnboundGenericType)
            {
                var interfaces = named.AllInterfaces.Prepend(named).ToArray();
                var dictionary = interfaces.FirstOrDefault(candidate => candidate.OriginalDefinition.ToDisplayString() is
                    "System.Collections.Generic.IDictionary<TKey, TValue>" or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>");
                var enumerable = interfaces.FirstOrDefault(candidate => candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
                if (dictionary is not null)
                {
                    node.Children.Add(dictionary.TypeArguments[1]);
                    node.Elements = "global::System.Linq.Enumerable.Select((global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<" +
                        Type(dictionary.TypeArguments[0]) + ", " + Type(dictionary.TypeArguments[1]) + ">>)value, static pair => (object?)pair.Value)";
                }
                else if (enumerable is not null && named.SpecialType != SpecialType.System_String)
                {
                    node.Children.Add(enumerable.TypeArguments[0]);
                    node.Elements = "global::System.Linq.Enumerable.Cast<object?>((global::System.Collections.IEnumerable)value)";
                }
                else if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                    node.Children.Add(named.TypeArguments[0]);
                else if (named.SpecialType == SpecialType.None && named.TypeKind is not (TypeKind.Delegate or TypeKind.Error) &&
                    named.ContainingNamespace.ToDisplayString() != "System" &&
                    !named.ContainingNamespace.ToDisplayString().StartsWith("System.", StringComparison.Ordinal))
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    for (var current = named; current is not null; current = current.BaseType)
                    {
                        foreach (var member in current.GetMembers())
                        {
                            if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public || !names.Add(member.Name))
                                continue;
                            if (member is IPropertySymbol { IsIndexer: false } property)
                            {
                                node.Members.Add(member);
                                node.Children.Add(property.Type);
                            }
                            else if (member is IFieldSymbol field)
                            {
                                node.Members.Add(member);
                                node.Children.Add(field.Type);
                            }
                        }
                    }
                    foreach (var attribute in named.GetAttributes())
                        if (attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonDerivedTypeAttribute" &&
                            attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol derived)
                            node.Children.Add(derived);
                }
            }
            foreach (var child in node.Children)
                pending.Enqueue(child);
        }
        return nodes;
    }

    sealed class Node(ITypeSymbol type)
    {
        internal ITypeSymbol Type { get; } = type;
        internal List<ISymbol> Members { get; } = [];
        internal List<ITypeSymbol> Children { get; } = [];
        internal string? Elements { get; set; }
    }

    static string Type(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    internal static string? CountExpression(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String || type is IArrayTypeSymbol)
            return "((" + Type(type) + ")value).Length";
        if (type is not INamedTypeSymbol named)
            return null;
        var collection = named.AllInterfaces.Prepend(named).FirstOrDefault(candidate => candidate.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.ICollection<T>" or "System.Collections.Generic.IReadOnlyCollection<T>");
        return collection is null ? null : "((" + Type(collection) + ")value).Count";
    }

    static IEnumerable<AttributeData> Attributes(ISymbol symbol)
    {
        var own = symbol.GetAttributes();
        foreach (var attribute in own)
            yield return attribute;
        if (symbol is IPropertySymbol property)
        {
            var constructors = property.ContainingType.InstanceConstructors.Where(ctor => ctor.DeclaredAccessibility == Accessibility.Public).ToArray();
            var binding = constructors.FirstOrDefault(ctor => ctor.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonConstructorAttribute"))
                ?? (constructors.Length == 1 ? constructors[0] : null);
            var parameter = binding?.Parameters.FirstOrDefault(candidate => string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase) &&
                SymbolEqualityComparer.Default.Equals(candidate.Type, property.Type));
            if (parameter is not null)
                foreach (var attribute in parameter.GetAttributes())
                    if (!own.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute.AttributeClass)))
                        yield return attribute;
        }
        if (symbol is IPropertySymbol { OverriddenProperty: { } overridden })
            foreach (var attribute in Attributes(overridden))
                if (!own.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute.AttributeClass)))
                    yield return attribute;
    }

    static void AppendBindingFacts(StringBuilder source, ISymbol member, ITypeSymbol memberType, INamedTypeSymbol contract)
    {
        var attributes = Attributes(member).ToArray();
        var defaultValue = attributes.FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DefaultValueAttribute");
        if (defaultValue is { ConstructorArguments.Length: 1 })
        {
            source.Append("HasDefault = true, DefaultValue = ");
            if (defaultValue.ConstructorArguments[0].IsNull)
                source.AppendLine("null,");
            else
                source.Append('(').Append(Type(memberType)).Append(")(").Append(Constant(defaultValue.ConstructorArguments[0])).AppendLine("),");
        }
        var constructor = contract.InstanceConstructors.Where(ctor => ctor.DeclaredAccessibility == Accessibility.Public).ToArray();
        var binding = constructor.FirstOrDefault(ctor => ctor.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonConstructorAttribute"))
            ?? (constructor.Length == 1 ? constructor[0] : null);
        var parameter = binding?.Parameters.FirstOrDefault(candidate => string.Equals(candidate.Name, member.Name, StringComparison.OrdinalIgnoreCase) &&
            SymbolEqualityComparer.Default.Equals(candidate.Type, memberType));
        var validates = ValidationRules(member).Any();
        if (parameter is not null && validates)
            source.AppendLine("ConstructorBound = true,");
        if (parameter is { HasExplicitDefaultValue: true })
            source.Append("HasBindingDefault = true, BindingDefaultValue = ").Append(Value(parameter.ExplicitDefaultValue, parameter.Type)).AppendLine(",");
        else if (parameter is not null && validates)
            source.Append("BindingDefaultValue = default(").Append(Type(parameter.Type)).AppendLine("),");
        else if (parameter is null && KnownInitializer(member, memberType, binding) is { } initializer)
            source.Append("HasBindingDefault = true, BindingDefaultValue = ").Append(initializer).AppendLine(",");
        var ignore = attributes.FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute");
        var condition = ignore is null ? -1 : ignore.NamedArguments.FirstOrDefault(argument => argument.Key == "Condition").Value.Value as int? ?? 1;
        if (condition is 1 or 5)
            source.AppendLine("Input = false,");
        if (condition is 1 or 4)
            source.AppendLine("Output = false,");
    }

    static string? KnownInitializer(ISymbol member, ITypeSymbol type, IMethodSymbol? constructor)
    {
        // Metadata must not instantiate a DTO or evaluate arbitrary constructor/initializer code.
        if (constructor is null || !constructor.IsImplicitlyDeclared)
            return null;
        var syntax = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        var initializer = syntax switch
        {
            PropertyDeclarationSyntax property => property.Initializer?.Value,
            VariableDeclaratorSyntax field => field.Initializer?.Value,
            _ => null
        };
        if (initializer is LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.DefaultLiteralExpression))
                return "default(" + Type(type) + ")";
            if (literal.Token.Value is null)
                return Value(null, type);
            var scalar = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;
            if (scalar.SpecialType == SpecialType.None && scalar.TypeKind != TypeKind.Enum)
                return literal.Token.Value is null && !type.IsValueType ? "null" : null;
            return "(" + Type(type) + ")(" + Value(literal.Token.Value, scalar) + ")";
        }
        var hasRule = ValidationRules(member).Any() || Attributes(member).Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DefaultValueAttribute");
        if (initializer is null && hasRule && syntax is PropertyDeclarationSyntax { AccessorList: { } accessors } &&
            accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null))
            return "default(" + Type(type) + ")";
        return null;
    }

    internal static bool IsValidationAttribute(string? name) => name is
        "System.ComponentModel.DataAnnotations.RequiredAttribute" or
        "System.ComponentModel.DataAnnotations.RangeAttribute" or
        "System.ComponentModel.DataAnnotations.MinLengthAttribute" or
        "System.ComponentModel.DataAnnotations.MaxLengthAttribute" or
        "System.ComponentModel.DataAnnotations.StringLengthAttribute" or
        "System.ComponentModel.DataAnnotations.RegularExpressionAttribute" or
        "System.ComponentModel.DataAnnotations.EmailAddressAttribute" or
        "System.ComponentModel.DataAnnotations.UrlAttribute";

    static IEnumerable<string> ValidationRules(ISymbol symbol)
    {
        foreach (var attribute in Attributes(symbol))
        {
            var name = attribute.AttributeClass?.ToDisplayString();
            if (!IsValidationAttribute(name))
                continue;
            var arguments = string.Join(", ", attribute.ConstructorArguments.Select(Constant));
            var settings = string.Join(", ", attribute.NamedArguments.Select(argument => argument.Key + " = " + Constant(argument.Value)));
            yield return "new global::" + name + "(" + arguments + ") { " + settings + " }";
        }
    }

    static string Constant(TypedConstant constant)
    {
        if (constant.IsNull)
            return "null";
        if (constant.Kind == TypedConstantKind.Type && constant.Value is ITypeSymbol type)
            return "typeof(" + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")";
        return Value(constant.Value, constant.Type);
    }

    static string Value(object? value, ITypeSymbol? type)
    {
        if (value is null)
            return type is { IsValueType: true } && type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }
                ? "default(" + Type(type) + ")" : "null";
        if (value is string text)
            return Literal(text);
        if (value is bool boolean)
            return boolean ? "true" : "false";
        if (value is char character)
            return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(character, true);
        if (value is float single && (float.IsNaN(single) || float.IsInfinity(single)))
            return "global::System.Single." + (float.IsNaN(single) ? "NaN" : single > 0 ? "PositiveInfinity" : "NegativeInfinity");
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        var literal = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
        var suffix = value is double ? "D" : value is float ? "F" : value is decimal ? "M" : value is long ? "L" : value is ulong ? "UL" : value is uint ? "U" : "";
        if (value is double number && (double.IsNaN(number) || double.IsInfinity(number)))
            return "global::System.Double." + (double.IsNaN(number) ? "NaN" : number > 0 ? "PositiveInfinity" : "NegativeInfinity");
        return type?.TypeKind == TypeKind.Enum ? "(" + Type(type) + ")" + literal : literal + suffix;
    }

    static void AppendFacts(StringBuilder source, ISymbol symbol)
    {
        if (Description(symbol) is { } description)
            source.Append("Description = ").Append(Literal(description)).AppendLine(",");
        if (Title(symbol) is { } title)
            source.Append("Title = ").Append(Literal(title)).AppendLine(",");
        var obsolete = Attributes(symbol).FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute");
        if (obsolete is not null)
        {
            source.AppendLine("Deprecated = true,");
            if (obsolete.ConstructorArguments.FirstOrDefault().Value is string message)
                source.Append("DeprecationMessage = ").Append(Literal(message)).AppendLine(",");
        }
    }

    static string? Title(ISymbol symbol)
    {
        var display = Attributes(symbol).FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.DisplayAttribute");
        var name = display?.NamedArguments.FirstOrDefault(argument => argument.Key == "Name").Value.Value as string;
        if (display?.NamedArguments.Any(argument => argument.Key == "ResourceType" && !argument.Value.IsNull) == true)
            name = null;
        return name ?? Attributes(symbol).FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DisplayNameAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;
    }

    static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, true);
}

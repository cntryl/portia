using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Cntryl.Portia;

static class HttpBindingShape
{
    public enum TextParseKind
    {
        None,
        String,
        FormatProvider
    }

    public static readonly Regex RouteTokenPattern =
        new(@"\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)(?:[:=?][^}]*)?\}", RegexOptions.Compiled);

    public static IMethodSymbol? SinglePublicConstructor(INamedTypeSymbol request)
    {
        // A struct always has an implicit parameterless constructor; it is never the binding constructor.
        var constructors = request.Constructors
            .Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public && !constructor.IsStatic
                                  && !(request.IsValueType && constructor.IsImplicitlyDeclared
                                                           && constructor.Parameters.Length == 0))
            .ToArray();
        return constructors.Length == 1 ? constructors[0] : null;
    }

    // A token is optional only when its name or its last constraint ends in '?'; a '?' inside a constraint
    // argument such as regex(^[a-z]?$) does not make it optional.
    public static bool IsOptionalRouteToken(Match token) => token.Value.EndsWith("?}", StringComparison.Ordinal);

    // Generated binding constructs the request through its constructor alone, so required members it does
    // not set would not compile.
    public static bool HasUnsetRequiredMembers(INamedTypeSymbol request, IMethodSymbol constructor)
    {
        if (constructor.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString()
                                                         == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"))
            return false;
        for (var current = request; current is not null; current = current.BaseType)
        {
            if (current.GetMembers().Any(member => member is IPropertySymbol { IsRequired: true }
                                             or IFieldSymbol { IsRequired: true }))
                return true;
        }

        return false;
    }

    public static bool IsRouteParameter(string pattern, string name) => RouteTokenPattern.Matches(pattern).Cast<Match>()
        .Any(match => Normalize(match.Groups[1].Value) == Normalize(name));

    public static bool IsSupportedParameter(IParameterSymbol parameter, bool textBound)
    {
        if (parameter.RefKind != RefKind.None)
            return false;
        if (!textBound)
            return true;
        var type = UnwrapNullable(parameter.Type);
        return type.SpecialType == SpecialType.System_String || type.TypeKind == TypeKind.Enum ||
               GetTextParseKind(type) != TextParseKind.None;
    }

    public static TextParseKind GetTextParseKind(ITypeSymbol type)
    {
        type = UnwrapNullable(type);
        var simple = false;
        foreach (var method in type.GetMembers("TryParse").OfType<IMethodSymbol>())
        {
            if (!method.IsStatic || method.IsGenericMethod || method.MethodKind != MethodKind.Ordinary ||
                method.DeclaredAccessibility != Accessibility.Public ||
                method.ReturnType.SpecialType != SpecialType.System_Boolean ||
                method.Parameters.Length is not (2 or 3) ||
                method.Parameters[0].RefKind != RefKind.None ||
                method.Parameters[0].Type.SpecialType != SpecialType.System_String ||
                method.Parameters[method.Parameters.Length - 1].RefKind != RefKind.Out ||
                !SymbolEqualityComparer.Default.Equals(method.Parameters[method.Parameters.Length - 1].Type, type))
                continue;

            if (method.Parameters.Length == 2)
            {
                simple = true;
                continue;
            }

            if (method.Parameters[1].RefKind == RefKind.None && IsFormatProvider(method.Parameters[1].Type))
                return TextParseKind.FormatProvider;
        }

        return simple ? TextParseKind.String : TextParseKind.None;
    }

    static bool IsFormatProvider(ITypeSymbol type) => type is INamedTypeSymbol
    {
        MetadataName: "IFormatProvider",
        ContainingNamespace: { } containingNamespace
    } && containingNamespace.ToDisplayString() == "System";

    static ITypeSymbol UnwrapNullable(ITypeSymbol type) => type is INamedTypeSymbol
    {
        OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
    } nullable
        ? nullable.TypeArguments[0]
        : type;

    public static string Normalize(string name) => name.Replace("_", string.Empty).ToLowerInvariant();
}

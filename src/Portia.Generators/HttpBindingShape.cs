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
        var constructors = request.Constructors
            .Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public && !constructor.IsStatic)
            .ToArray();
        return constructors.Length == 1 ? constructors[0] : null;
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

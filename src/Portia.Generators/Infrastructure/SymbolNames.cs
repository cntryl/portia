using Microsoft.CodeAnalysis;

namespace Cntryl.Portia;

// Name comparisons run for every base type and interface of every analyzed type on every edit, so they compare
// symbol names in place instead of formatting display strings.
static class SymbolNames
{
    // Whether type is exactly the metadata name given, such as "Cntryl.Portia.Projector" or
    // "Grpc.Core.ClientBase`1"; nested types are written with '+'.
    public static bool Is(INamedTypeSymbol type, string metadataName)
    {
        var end = metadataName.Length;
        var current = type.OriginalDefinition;
        while (true)
        {
            if (end <= 0)
                return false;
            var separator = metadataName.LastIndexOfAny(['.', '+'], end - 1);
            if (!Segment(metadataName, separator + 1, end, current.MetadataName))
                return false;
            var nested = separator >= 0 && metadataName[separator] == '+';
            end = separator;
            if (current.ContainingType is { } containing)
            {
                if (!nested)
                    return false;
                current = containing;
                continue;
            }

            return !nested && current.ContainingNamespace is { } space && NamespaceMatches(space, metadataName, end);
        }
    }

    public static bool IsInNamespace(INamedTypeSymbol type, string @namespace) =>
        type.ContainingNamespace is { } space && NamespaceMatches(space, @namespace, @namespace.Length);

    static bool NamespaceMatches(INamespaceSymbol space, string name, int end)
    {
        for (; !space.IsGlobalNamespace; space = space.ContainingNamespace!)
        {
            if (end <= 0)
                return false;
            var separator = name.LastIndexOf('.', end - 1);
            if (!Segment(name, separator + 1, end, space.Name))
                return false;
            end = separator;
        }

        return end <= 0;
    }

    static bool Segment(string name, int start, int end, string expected) =>
        end - start == expected.Length && string.CompareOrdinal(name, start, expected, 0, expected.Length) == 0;
}

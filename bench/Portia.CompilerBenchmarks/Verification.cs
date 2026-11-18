namespace Cntryl.Portia.CompilerBenchmarks;

static class Verification
{
    public static void Coverage(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Equal("component coverage", expected, actual);

    public static void Changes(IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after, IEnumerable<string> expected)
    {
        var changed = before.Keys.Union(after.Keys, StringComparer.Ordinal).Where(key =>
            !before.TryGetValue(key, out var left) || !after.TryGetValue(key, out var right) || left != right);
        Equal("output changes", expected, changed);
    }

    public static void Actions(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Equal("code actions", expected, actual);

    static void Equal(string label, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var left = expected.Order(StringComparer.Ordinal).ToArray();
        var right = actual.Order(StringComparer.Ordinal).ToArray();
        if (!left.SequenceEqual(right, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Unexpected {label}. Expected: {string.Join(", ", left)}; actual: {string.Join(", ", right)}.");
    }
}

namespace Cntryl.Portia;

/// <summary>
///     Names the per-component <c>Add&lt;Component&gt;()</c> methods the registration generators emit.
///     Two components in one compilation can share a simple name (different namespaces, or a nested
///     type), so a colliding name is disambiguated by its fully qualified type name rather than by
///     declaration order — the emitted API has to stay stable across builds.
/// </summary>
static class GeneratedRegistrationNames
{
    public static Dictionary<string, string> Resolve(IEnumerable<string> fullyQualifiedTypeNames)
    {
        var names = new Dictionary<string, string>();
        var bySimpleName = fullyQualifiedTypeNames
            .Distinct(StringComparer.Ordinal)
            .OrderBy(type => type, StringComparer.Ordinal)
            .GroupBy(SimpleName, StringComparer.Ordinal);

        foreach (var group in bySimpleName)
        {
            var colliding = group.Count() > 1;
            foreach (var type in group)
                names[type] = colliding ? "Add" + Qualify(type) : "Add" + group.Key;
        }

        return names;
    }

    static string SimpleName(string fullyQualifiedTypeName)
    {
        // A type in the global namespace is just "global::Name", with no '.' to split on.
        var name = Unqualify(fullyQualifiedTypeName);
        var separator = name.LastIndexOfAny(['.', '+']);
        return separator < 0 ? name : name.Substring(separator + 1);
    }

    static string Unqualify(string fullyQualifiedTypeName) =>
        fullyQualifiedTypeName.StartsWith("global::", StringComparison.Ordinal)
            ? fullyQualifiedTypeName.Substring("global::".Length)
            : fullyQualifiedTypeName;

    // "global::Acme.Billing.Charge" -> "AcmeBillingCharge": every identifier in the qualified
    // name, so two same-named components stay distinguishable and readable.
    static string Qualify(string fullyQualifiedTypeName) =>
        string.Concat(Unqualify(fullyQualifiedTypeName).Split('.', '+').Where(part => part.Length > 0));
}

using System.Security.Cryptography;
using System.Text;

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
        var types = fullyQualifiedTypeNames
            .Distinct(StringComparer.Ordinal)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToArray();
        var qualified = new HashSet<string>(StringComparer.Ordinal);

        // Collisions are decided on the emitted name, not the simple name: Billing.Charge and
        // Sales.Charge qualify to AddBillingCharge, which Other.BillingCharge already claims, so a
        // colliding type qualifies until no emitted name is shared.
        bool qualifiedMore;
        do
        {
            qualifiedMore = false;
            foreach (var type in types.GroupBy(Candidate, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1).SelectMany(group => group).ToArray())
            {
                qualifiedMore |= qualified.Add(type);
            }
        } while (qualifiedMore);

        // Distinct qualified names can still concatenate alike (A.BC and AB.C). Reserve every
        // candidate before assigning suffixes, including names already claimed by other types.
        var names = new Dictionary<string, string>();
        var reserved = new HashSet<string>(types.Select(Candidate), StringComparer.Ordinal);
        foreach (var group in types.GroupBy(Candidate, StringComparer.Ordinal))
        {
            foreach (var type in group)
            {
                if (group.Count() == 1)
                {
                    names[type] = group.Key;
                    continue;
                }

                var baseName = group.Key + "_" + StableSuffix(type);
                var name = baseName;
                for (var suffix = 2; !reserved.Add(name); suffix++)
                    name = baseName + "_" + suffix;
                names[type] = name;
            }
        }

        return names;

        string Candidate(string type) => "Add" + (qualified.Contains(type) ? Qualify(type) : SimpleName(type));
    }

    static string StableSuffix(string fullyQualifiedTypeName)
    {
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(fullyQualifiedTypeName));
        return BitConverter.ToString(bytes, 0, 4).Replace("-", "");
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

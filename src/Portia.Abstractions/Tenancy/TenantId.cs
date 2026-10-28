using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>
///     Identifies a tenant — conventionally the same value used as a request or event stream's
///     realm segment, since realm is already the framework's per-tenant wildcard everywhere else
///     (routing, event stream patterns). A multi-tenant runner runs one instance of whatever a
///     caller supplies per active <see cref="TenantId" />.
///     <para>
///         A request can declare a <see cref="TenantId" /> member directly: it binds from a route token,
///         query value, or JSON body like <see cref="Uuid" /> does, and text that is not a realm segment
///         is refused at that boundary rather than inside a handler.
///     </para>
/// </summary>
/// <param name="Value">The tenant identity, matching a realm segment.</param>
[JsonConverter(typeof(TenantIdJsonConverter))]
public readonly record struct TenantId(string Value) : ISpanParsable<TenantId>, ISpanFormattable
{
    /// <summary>Gets the tenant identity, a realm segment other than the reserved <c>{tenant}</c> template token.</summary>
    /// <exception cref="ArgumentException">The value is not a valid tenant realm segment.</exception>
    public string Value { get; init => field = Validate(value, nameof(Value)); } = Validate(Value, nameof(Value));

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format,
        IFormatProvider? provider)
    {
        var value = ToString().AsSpan();
        if (!value.TryCopyTo(destination))
        {
            charsWritten = 0;
            return false;
        }

        charsWritten = value.Length;
        return true;
    }

    /// <summary>Parses a tenant identity.</summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="provider">Ignored; a tenant identity is culture-invariant.</param>
    /// <returns>The tenant.</returns>
    /// <exception cref="FormatException"><paramref name="s" /> is not a valid tenant realm segment.</exception>
    public static TenantId Parse(string s, IFormatProvider? provider) =>
        TryParse(s, provider, out var tenant) ? tenant : throw new FormatException(InvalidMessage(s));

    /// <summary>Parses a tenant identity.</summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="provider">Ignored; a tenant identity is culture-invariant.</param>
    /// <returns>The tenant.</returns>
    /// <exception cref="FormatException"><paramref name="s" /> is not a valid tenant realm segment.</exception>
    public static TenantId Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        TryParse(s, provider, out var tenant) ? tenant : throw new FormatException(InvalidMessage(s.ToString()));

    /// <summary>Tries to parse a tenant identity.</summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="provider">Ignored; a tenant identity is culture-invariant.</param>
    /// <param name="result">The tenant, or <c>default</c> when the text is not a valid realm segment.</param>
    /// <returns>Whether the text was a valid tenant realm segment.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out TenantId result)
    {
        if (s is not null && IsValid(s))
        {
            result = new TenantId(s);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Tries to parse a tenant identity.</summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="provider">Ignored; a tenant identity is culture-invariant.</param>
    /// <param name="result">The tenant, or <c>default</c> when the text is not a valid realm segment.</param>
    /// <returns>Whether the text was a valid tenant realm segment.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out TenantId result) =>
        TryParse(s.ToString(), provider, out result);

    // A tenant ID becomes a realm segment wherever it is used, so it follows the realm rules of
    // EventStreamPattern.ForPattern — the same rules WorkloadIdentity enforces on its tenant.
    static bool IsValid(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains('/')
        && !value.Contains('*')
        && !string.Equals(value, EventStreamPattern.TenantTemplateRealm, StringComparison.Ordinal);

    static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('/') || value.Contains('*'))
        {
            throw new ArgumentException("A tenant ID cannot contain '/' or '*'.", parameterName);
        }

        return string.Equals(value, EventStreamPattern.TenantTemplateRealm, StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"The tenant ID '{EventStreamPattern.TenantTemplateRealm}' is reserved for tenant templates.",
                parameterName)
            : value;
    }

    static string InvalidMessage(string value) =>
        $"'{value}' is not a valid tenant ID: it must be a nonblank realm segment without '/' or '*', other than '{EventStreamPattern.TenantTemplateRealm}'.";
}

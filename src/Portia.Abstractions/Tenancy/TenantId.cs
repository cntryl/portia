namespace Cntryl.Portia;

/// <summary>
///     Identifies a tenant — conventionally the same value used as a request or event stream's
///     realm segment, since realm is already the framework's per-tenant wildcard everywhere else
///     (routing, event stream patterns). A multi-tenant runner runs one instance of whatever a
///     caller supplies per active <see cref="TenantId" />.
/// </summary>
/// <param name="Value">The tenant identity, matching a realm segment.</param>
public readonly record struct TenantId(string Value)
{
    /// <summary>Gets the tenant identity, a realm segment other than the reserved <c>{tenant}</c> template token.</summary>
    /// <exception cref="ArgumentException">The value is not a valid tenant realm segment.</exception>
    public string Value { get; init => field = Validate(value, nameof(Value)); } = Validate(Value, nameof(Value));

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;

    // A tenant ID becomes a realm segment wherever it is used, so it follows the realm rules of
    // EventStreamPattern.ForPattern — the same rules WorkloadIdentity enforces on its tenant.
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
}

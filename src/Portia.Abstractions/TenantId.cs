namespace Cntryl.Portia;

/// <summary>
/// Identifies a tenant — conventionally the same value used as a request or event stream's
/// realm segment, since realm is already the framework's per-tenant wildcard everywhere else
/// (routing, event stream patterns). A multi-tenant runner runs one instance of whatever a
/// caller supplies per active <see cref="TenantId" />.
/// </summary>
/// <param name="Value">The tenant identity, matching a realm segment.</param>
public readonly record struct TenantId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

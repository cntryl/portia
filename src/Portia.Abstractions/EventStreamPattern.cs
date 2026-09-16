namespace Cntryl.Portia;

/// <summary>
///     Selects event streams inside one realm by optional area and resource segments. A null segment
///     matches every value at that level, so a pattern can name one stream, a whole area, or a whole
///     realm. See <see cref="EventStreamAddress" /> for what the three segments mean.
/// </summary>
public sealed record EventStreamPattern
{
    const string TenantTemplateRealm = "{tenant}";

    EventStreamPattern(string realm, string? area, string? resource, bool tenantTemplate = false)
    {
        Realm = tenantTemplate ? TenantTemplateRealm : ValidateRequiredSegment(realm, nameof(realm));
        IsTenantTemplate = tenantTemplate;
        Area = ValidateOptionalSegment(area, nameof(area));
        Resource = ValidateOptionalSegment(resource, nameof(resource));
        Scope = Area == null && Resource is not null
            ? throw new ArgumentException("A resource-specific stream pattern must also specify its area.",
                nameof(resource))
            : GetScope(Area, Resource);
    }

    /// <summary>
    ///     Gets the selected top-level scope.
    /// </summary>
    public string Realm { get; }

    /// <summary>
    ///     Gets the selected area, or <see langword="null" /> when every area is selected.
    /// </summary>
    public string? Area { get; }

    /// <summary>
    ///     Gets the selected resource, or <see langword="null" /> when every resource is selected.
    /// </summary>
    public string? Resource { get; }

    /// <summary>
    ///     Gets whether this pattern is an unbound tenant template. Tenant templates may only be
    ///     run by a <c>PerTenant</c> hosted workload, which binds the actual tenant before reading.
    /// </summary>
    public bool IsTenantTemplate { get; }

    internal EventStreamPatternScope Scope { get; }

    /// <summary>
    ///     Creates a realm-scoped stream pattern from optional exact area and resource segments.
    /// </summary>
    /// <param name="realm">The exact realm. Cross-realm reads are not part of the common event-reader contract.</param>
    /// <param name="area">The exact area, or <see langword="null" /> or empty for every area.</param>
    /// <param name="resource">The exact resource, or <see langword="null" /> or empty for every resource.</param>
    /// <returns>The stream pattern.</returns>
    public static EventStreamPattern ForPattern(
        string realm,
        string? area = null,
        string? resource = null)
    {
        if (string.Equals(realm, TenantTemplateRealm, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The realm '{TenantTemplateRealm}' is reserved for tenant templates; use ForTenant().",
                nameof(realm));
        }

        return new EventStreamPattern(realm, area, resource);
    }

    /// <summary>Creates an unbound pattern that a per-tenant hosted workload binds to its tenant.</summary>
    /// <param name="area">The exact area, or <see langword="null" /> or empty for every area.</param>
    /// <param name="resource">The exact resource, or <see langword="null" /> or empty for every resource.</param>
    /// <returns>The unbound tenant stream template.</returns>
    public static EventStreamPattern ForTenant(string? area = null, string? resource = null)
        => new(TenantTemplateRealm, area, resource, true);

    internal EventStreamPattern BindTenant(TenantId tenant)
    {
        if (!IsTenantTemplate)
        {
            throw new InvalidOperationException("Only an unbound tenant stream template can be bound.");
        }

        return new EventStreamPattern(tenant.Value, Area, Resource);
    }

    /// <summary>
    ///     Returns the canonical stream selector.
    /// </summary>
    /// <returns>The canonical stream selector.</returns>
    public override string ToString() => $"stream://{Realm}/{Area ?? "*"}/{Resource ?? "*"}";

    static string ValidateRequiredSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        return value.Contains('/') || value.Contains('*')
            ? throw new ArgumentException("A supplied stream pattern segment cannot contain '/' or '*'.", parameterName)
            : value;
    }

    static string? ValidateOptionalSegment(string? value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Contains('/') || value.Contains('*')
                ? throw new ArgumentException("A supplied stream pattern segment cannot contain '/' or '*'.",
                    parameterName)
                : value;
    }

    static EventStreamPatternScope GetScope(string? area, string? resource)
    {
        return area is not null && resource is not null
            ? EventStreamPatternScope.Resource
            : area is not null
                ? EventStreamPatternScope.Area
                : EventStreamPatternScope.Realm;
    }
}

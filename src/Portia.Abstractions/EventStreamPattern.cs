namespace Cntryl.Portia;

/// <summary>
/// Selects event streams by optional Fitz realm, area, and resource segments.
/// </summary>
public sealed record EventStreamPattern
{
    EventStreamPattern(string? realm, string? area, string? resource)
    {
        Realm = ValidateOptionalSegment(realm, nameof(realm));
        Area = ValidateOptionalSegment(area, nameof(area));
        Resource = ValidateOptionalSegment(resource, nameof(resource));
        Scope = GetScope(Realm, Area, Resource);
    }

    /// <summary>
    /// Gets the selected Fitz realm.
    /// </summary>
    public string? Realm { get; }

    /// <summary>
    /// Gets the selected Fitz area, or <see langword="null" /> when every area is selected.
    /// </summary>
    public string? Area { get; }

    /// <summary>
    /// Gets the selected Fitz resource, or <see langword="null" /> when every resource is selected.
    /// </summary>
    public string? Resource { get; }

    internal EventStreamPatternScope Scope { get; }

    /// <summary>
    /// Creates a stream pattern from optional exact segments.
    /// </summary>
    /// <param name="realm">The exact realm, or <see langword="null" /> or empty for every realm.</param>
    /// <param name="area">The exact area, or <see langword="null" /> or empty for every area.</param>
    /// <param name="resource">The exact resource, or <see langword="null" /> or empty for every resource.</param>
    /// <returns>The stream pattern.</returns>
    public static EventStreamPattern ForPattern(
        string? realm = null,
        string? area = null,
        string? resource = null) => new(realm, area, resource);

    /// <summary>
    /// Returns the canonical Fitz stream selector.
    /// </summary>
    /// <returns>The canonical stream selector.</returns>
    public override string ToString() => Realm is null && Area is null && Resource is null
        ? "stream://**"
        : $"stream://{Realm ?? "*"}/{Area ?? "*"}/{Resource ?? "*"}";

    static string? ValidateOptionalSegment(string? value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Contains('/') || value.Contains('*')
                ? throw new ArgumentException("A supplied stream pattern segment cannot contain '/' or '*'.", parameterName)
                : value;
    }

    static EventStreamPatternScope GetScope(string? realm, string? area, string? resource)
    {
        return realm is not null && area is not null && resource is not null
            ? EventStreamPatternScope.Resource
            : realm is not null && area is not null
                ? EventStreamPatternScope.Area
                : realm is not null
                    ? EventStreamPatternScope.Realm
                    : EventStreamPatternScope.Global;
    }
}

enum EventStreamPatternScope
{
    Resource,
    Area,
    Realm,
    Global,
}

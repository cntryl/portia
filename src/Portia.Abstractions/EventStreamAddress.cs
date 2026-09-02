namespace Cntryl.Portia;

/// <summary>
/// Identifies an aggregate stream by its Fitz realm, area, and resource.
/// </summary>
public sealed record EventStreamAddress
{
    /// <summary>
    /// Creates an aggregate stream identity.
    /// </summary>
    /// <param name="realm">The Fitz realm.</param>
    /// <param name="area">The Fitz area.</param>
    /// <param name="resource">The Fitz resource.</param>
    public EventStreamAddress(string realm, string area, string resource)
    {
        Realm = ValidateSegment(realm, nameof(realm));
        Area = ValidateSegment(area, nameof(area));
        Resource = ValidateSegment(resource, nameof(resource));
    }

    /// <summary>
    /// Gets the Fitz realm.
    /// </summary>
    public string Realm { get; }

    /// <summary>
    /// Gets the Fitz area.
    /// </summary>
    public string Area { get; }

    /// <summary>
    /// Gets the Fitz resource.
    /// </summary>
    public string Resource { get; }

    /// <summary>
    /// Returns the canonical Fitz stream route.
    /// </summary>
    /// <returns>The canonical stream route.</returns>
    public override string ToString() => $"stream://{Realm}/{Area}/{Resource}";

    /// <summary>
    /// Parses a canonical Fitz stream route.
    /// </summary>
    /// <param name="route">The canonical route.</param>
    /// <returns>The parsed stream address.</returns>
    public static EventStreamAddress Parse(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        const string prefix = "stream://";

        if (!route.StartsWith(prefix, StringComparison.Ordinal))
            throw new FormatException($"'{route}' is not a canonical Fitz stream route.");

        var segments = route[prefix.Length..].Split('/');
        return segments.Length == 3
            ? new EventStreamAddress(segments[0], segments[1], segments[2])
            : throw new FormatException($"'{route}' is not a canonical Fitz stream route.");
    }

    static string ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        return value.Contains('/') || value.Contains('*')
            ? throw new ArgumentException("A stream segment cannot contain '/' or '*'.", parameterName)
            : value;
    }
}

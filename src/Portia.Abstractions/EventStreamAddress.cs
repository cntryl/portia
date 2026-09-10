namespace Cntryl.Portia;

/// <summary>
///     Identifies one aggregate's event stream by three naming segments, widest first: the realm
///     (the top-level scope, typically an application or tenant), the area (a grouping of related
///     streams within it), and the resource (the individual stream). An event store adapter maps
///     these onto whatever its backing technology calls them.
/// </summary>
public sealed record EventStreamAddress
{
    /// <summary>
    ///     Creates an aggregate stream identity.
    /// </summary>
    /// <param name="realm">The stream's top-level scope.</param>
    /// <param name="area">The grouping of related streams within the realm.</param>
    /// <param name="resource">The individual stream within the area.</param>
    public EventStreamAddress(string realm, string area, string resource)
    {
        Realm = ValidateSegment(realm, nameof(realm));
        Area = ValidateSegment(area, nameof(area));
        Resource = ValidateSegment(resource, nameof(resource));
    }

    /// <summary>
    ///     Gets the stream's top-level scope.
    /// </summary>
    public string Realm { get; }

    /// <summary>
    ///     Gets the grouping of related streams within the realm.
    /// </summary>
    public string Area { get; }

    /// <summary>
    ///     Gets the individual stream within the area.
    /// </summary>
    public string Resource { get; }

    /// <summary>
    ///     Returns the canonical stream route.
    /// </summary>
    /// <returns>The canonical stream route.</returns>
    public override string ToString() => $"stream://{Realm}/{Area}/{Resource}";

    /// <summary>
    ///     Parses a canonical stream route.
    /// </summary>
    /// <param name="route">The canonical route.</param>
    /// <returns>The parsed stream address.</returns>
    public static EventStreamAddress Parse(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        const string prefix = "stream://";

        if (!route.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new FormatException($"'{route}' is not a canonical stream route.");
        }
        else
        {
            var segments = route[prefix.Length..].Split('/');
            return segments.Length == 3
                ? new EventStreamAddress(segments[0], segments[1], segments[2])
                : throw new FormatException($"'{route}' is not a canonical stream route.");
        }
    }

    static string ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        return value.Contains('/') || value.Contains('*')
            ? throw new ArgumentException("A stream segment cannot contain '/' or '*'.", parameterName)
            : value;
    }
}

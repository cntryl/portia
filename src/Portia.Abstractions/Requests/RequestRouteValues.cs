namespace Cntryl.Portia;

/// <summary>
///     Supplies the route segments a request's <see cref="RequestRouteAttribute" /> left as
///     <see cref="RequestRouteAttribute.Wildcard" /> (most commonly the realm, which is often
///     contextual — e.g. the caller's tenant — rather than fixed per request type).
/// </summary>
/// <param name="Realm">The realm, if the request's route wildcards it.</param>
/// <param name="Area">The area, if the request's route wildcards it.</param>
/// <param name="Resource">The resource, if the request's route wildcards it.</param>
/// <param name="Operation">The operation, if the request's route wildcards it.</param>
public sealed record RequestRouteValues(
    string? Realm = null,
    string? Area = null,
    string? Resource = null,
    string? Operation = null)
{
    /// <summary>
    ///     Gets an empty set of route values, for a request whose route has no wildcarded segments.
    /// </summary>
    public static RequestRouteValues None { get; } = new();
}

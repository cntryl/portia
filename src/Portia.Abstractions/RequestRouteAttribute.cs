namespace Cntryl.Portia;

/// <summary>
/// Declares the realm, area, resource, and operation a request routes through when sent out of
/// process, independent of the transport (Fitz, a message broker, or anything else) that ends
/// up carrying it. Any segment may be <c>"*"</c>, meaning it is not known until the request is
/// sent and must be supplied via <see cref="RequestRouteValues" />.
/// </summary>
/// <param name="realm">The realm, or <c>"*"</c> if supplied per call.</param>
/// <param name="area">The area, or <c>"*"</c> if supplied per call.</param>
/// <param name="resource">The resource, or <c>"*"</c> if supplied per call.</param>
/// <param name="operation">The operation, or <c>"*"</c> if supplied per call.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RequestRouteAttribute(string realm, string area, string resource, string operation) : Attribute
{
    /// <summary>
    /// The wildcard value marking a segment that must be supplied per call.
    /// </summary>
    public const string Wildcard = "*";

    /// <summary>
    /// Gets the realm, or <see cref="Wildcard" /> if supplied per call.
    /// </summary>
    public string Realm { get; } = realm;

    /// <summary>
    /// Gets the area, or <see cref="Wildcard" /> if supplied per call.
    /// </summary>
    public string Area { get; } = area;

    /// <summary>
    /// Gets the resource, or <see cref="Wildcard" /> if supplied per call.
    /// </summary>
    public string Resource { get; } = resource;

    /// <summary>
    /// Gets the operation, or <see cref="Wildcard" /> if supplied per call.
    /// </summary>
    public string Operation { get; } = operation;
}

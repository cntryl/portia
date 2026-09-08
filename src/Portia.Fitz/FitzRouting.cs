namespace Cntryl.Portia;

/// <summary>
/// Builds Fitz wire routes from a request's <see cref="RequestRouteAttribute" />, deciding per
/// Fitz domain which segments the wire route actually carries (RPC and schedule routes carry
/// all four segments; Fitz queue and notice routes carry only realm/area/resource — Portia's
/// route contract stays uniform, Fitz's domain-specific route shapes are this adapter's
/// concern, not the contract's).
/// </summary>
static class FitzRouting
{
    /// <summary>
    /// Builds a 4-segment Fitz RPC route: <c>rpc://{realm}/{area}/{resource}/{operation}</c>.
    /// </summary>
    public static string ResolveRpcRoute(RequestTransportCatalog catalog, IRequestBase request, RequestRouteValues routeValues) =>
        ResolveRoute(catalog, "rpc", request, routeValues, includeOperation: true);

    /// <summary>
    /// Builds the RPC worker registration pattern for a request type, straight off its declared
    /// <see cref="RequestRouteAttribute" /> — unlike <see cref="ResolveRpcRoute" />, this needs no
    /// <see cref="RequestRouteValues" />, since a worker registers against the pattern (wildcard
    /// segments included, using the same <c>"*"</c> sentinel as the route declaration itself),
    /// not one resolved call's concrete route.
    /// </summary>
    public static string ResolveRpcWorkerPattern<TRequest>(RequestTransportCatalog catalog)
        where TRequest : IRequestBase
    {
        var route = catalog.Get(typeof(TRequest)).Route;
        return $"rpc://{route.Realm}/{route.Area}/{route.Resource}/{route.Operation}";
    }

    /// <summary>
    /// Builds a 3-segment Fitz queue route: <c>queue://{realm}/{area}/{resource}</c>. The
    /// request's declared operation, if any, is not part of a Fitz queue route.
    /// </summary>
    public static string ResolveQueueRoute(RequestTransportCatalog catalog, IRequestBase request, RequestRouteValues routeValues) =>
        ResolveRoute(catalog, "queue", request, routeValues, includeOperation: false);

    /// <summary>
    /// Builds a 3-segment Fitz notice route: <c>notice://{realm}/{area}/{resource}</c>. The
    /// request's declared operation, if any, is not part of a Fitz notice route.
    /// </summary>
    public static string ResolveNoticeRoute(RequestTransportCatalog catalog, IRequestBase request, RequestRouteValues routeValues) =>
        ResolveRoute(catalog, "notice", request, routeValues, includeOperation: false);

    /// <summary>
    /// Builds a 4-segment Fitz schedule route: <c>schedule://{realm}/{area}/{resource}/{operation}</c>.
    /// </summary>
    public static string ResolveScheduleRoute(RequestTransportCatalog catalog, IRequestBase request, RequestRouteValues routeValues) =>
        ResolveRoute(catalog, "schedule", request, routeValues, includeOperation: true);

    static string ResolveRoute(RequestTransportCatalog catalog, string scheme, IRequestBase request, RequestRouteValues routeValues, bool includeOperation)
    {
        var route = catalog.Get(request.GetType()).Route;
        var resolved = $"{scheme}://{Segment(route.Realm, routeValues.Realm, nameof(route.Realm))}"
            + $"/{Segment(route.Area, routeValues.Area, nameof(route.Area))}"
            + $"/{Segment(route.Resource, routeValues.Resource, nameof(route.Resource))}";

        return includeOperation
            ? $"{resolved}/{Segment(route.Operation, routeValues.Operation, nameof(route.Operation))}"
            : resolved;
    }

    static string Segment(string declared, string? suppliedValue, string segmentName)
    {
        var value = declared != RequestRouteAttribute.Wildcard
            ? declared
            : suppliedValue ?? throw new InvalidOperationException(
                $"Route segment '{segmentName}' is wildcarded and was not supplied in {nameof(RequestRouteValues)}.");
        return !string.IsNullOrWhiteSpace(value)
            && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '~')
            ? value
            : throw new InvalidOperationException($"Route segment '{segmentName}' contains unsupported characters.");
    }
}

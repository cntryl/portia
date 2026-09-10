namespace Cntryl.Portia;

abstract record FitzRoutedWorkerDefinition(string Route, string Scheme, int SegmentCount)
    : FitzWorkerDefinition(Validate(Route, Scheme, SegmentCount))
{
    static readonly Type[] RequiredServices =
        [typeof(IRequestBus), typeof(IRequestActorValidator), typeof(IRequestDeserializer)];

    internal override string Key => $"{Scheme}:{Route}";
    internal override IReadOnlyCollection<Type> Requirements => RequiredServices;

    /// <summary>Formats this kind's route from a request's declared segments.</summary>
    internal static string Format(string scheme, RequestRouteAttribute route, bool includeOperation) => includeOperation
        ? $"{scheme}://{route.Realm}/{route.Area}/{route.Resource}/{route.Operation}"
        : $"{scheme}://{route.Realm}/{route.Area}/{route.Resource}";

    static string Validate(string route, string scheme, int segmentCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var segments = route.StartsWith(scheme + "://", StringComparison.Ordinal)
            ? route[(scheme.Length + 3)..].Split('/')
            : [];
        return segments.Length == segmentCount && segments.All(segment => !string.IsNullOrWhiteSpace(segment))
            ? route
            : throw new ArgumentException($"Invalid {scheme} route '{route}'.", nameof(route));
    }
}

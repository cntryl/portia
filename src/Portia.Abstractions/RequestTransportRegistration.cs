namespace Cntryl.Portia;

/// <summary>
///     Describes one out-of-process-reachable request discovered at compile time — its route and
///     which transports it declared itself reachable through — so a host can wire up the RPC
///     workers, queue consumers, notice consumers, and schedule consumers it actually needs without
///     runtime assembly scanning or reflection.
/// </summary>
/// <param name="requestType">The concrete request type.</param>
/// <param name="transports">The transports the request declared itself reachable through.</param>
/// <param name="route">
///     The request's declared route (segments may be
///     <see cref="RequestRouteAttribute.Wildcard" />).
/// </param>
/// <param name="discriminator">The stable versioned wire discriminator, independent of routing.</param>
/// <param name="registerRpc">Generated typed RPC registration, when callable.</param>
/// <param name="resultType">The unary result or streaming item type, when present.</param>
public sealed class RequestTransportRegistration(
    Type requestType,
    RequestTransports transports,
    RequestRouteAttribute route,
    DiscriminatorAttribute discriminator,
    Func<IRequestRpcRegistrar, CancellationToken, ValueTask<IAsyncDisposable>>? registerRpc = null,
    Type? resultType = null)
{
    /// <summary>
    ///     Gets the concrete request type.
    /// </summary>
    public Type RequestType { get; } = requestType ?? throw new ArgumentNullException(nameof(requestType));

    /// <summary>
    ///     Gets the transports the request declared itself reachable through.
    /// </summary>
    public RequestTransports Transports { get; } = transports;

    /// <summary>
    ///     Gets the request's declared route (segments may be <see cref="RequestRouteAttribute.Wildcard" />).
    /// </summary>
    public RequestRouteAttribute Route { get; } = route ?? throw new ArgumentNullException(nameof(route));

    /// <summary>Gets the stable versioned wire discriminator, independent of routing.</summary>
    public DiscriminatorAttribute Discriminator { get; } =
        discriminator ?? throw new ArgumentNullException(nameof(discriminator));

    /// <summary>Gets the generated typed RPC registration, when callable.</summary>
    public Func<IRequestRpcRegistrar, CancellationToken, ValueTask<IAsyncDisposable>>? RegisterRpc { get; } =
        registerRpc;

    /// <summary>Gets the unary result or streaming item type, when the request produces one.</summary>
    public Type? ResultType { get; } = resultType;
}

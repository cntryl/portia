using Cntryl.Fitz.Abstractions.Domains.Rpc;

namespace Cntryl.Portia;

/// <summary>
/// Sends requests to an out-of-process handler over Fitz RPC.
/// </summary>
/// <remarks>
/// This deliberately imposes no timeout of its own on <see cref="IRpcClient.CallAsync" /> — how
/// (or whether) a call to a route with no registered worker fails is entirely Fitz's contract to
/// honor, not Portia's to second-guess with an opinionated default. Confirmed against a real
/// broker (see <c>FitzBrokerIntegrationTests.ShouldFailFastWhenNoWorkerIsRegisteredForRoute</c>):
/// Fitz itself fails a call to an unregistered route in milliseconds, not by hanging, so this
/// isn't a gap left open on faith. If a caller needs to bound how long it waits regardless, pass
/// a <see cref="CancellationToken" /> that cancels after however long is appropriate for that call.
/// </remarks>
/// <param name="rpc">The Fitz RPC client.</param>
/// <param name="requestSerializer">Serializes outbound requests.</param>
/// <param name="outcomeDeserializer">Deserializes inbound outcomes.</param>
public sealed class FitzRemoteRequestSender(
    IRpcClient rpc,
    IRequestSerializer requestSerializer,
    IRequestOutcomeDeserializer outcomeDeserializer) : IRemoteRequestSender
{
    readonly IRpcClient _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
    readonly IRequestSerializer _requestSerializer = requestSerializer ?? throw new ArgumentNullException(nameof(requestSerializer));
    readonly IRequestOutcomeDeserializer _outcomeDeserializer = outcomeDeserializer ?? throw new ArgumentNullException(nameof(outcomeDeserializer));

    /// <inheritdoc />
    public async ValueTask<Result> SendAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest, ICallable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        var route = FitzRouting.ResolveRpcRoute(request, routeValues);
        var body = _requestSerializer.Serialize(request, actorToken);
        var receivedFrame = await CallAsync(route, body, ct).ConfigureAwait(false);

        return _outcomeDeserializer.DeserializeOutcome(receivedFrame.Body);
    }

    /// <inheritdoc />
    public async ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        var route = FitzRouting.ResolveRpcRoute(request, routeValues);
        var body = _requestSerializer.Serialize(request, actorToken);
        var receivedFrame = await CallAsync(route, body, ct).ConfigureAwait(false);

        return _outcomeDeserializer.DeserializeResult<TOut>(receivedFrame.Body);
    }

    async ValueTask<RpcResponseFrame> CallAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        RpcResponseFrame? lastFrame = null;

        await foreach (var frame in _rpc.CallAsync(route, body, ct).WithCancellation(ct).ConfigureAwait(false))
            lastFrame = frame;

        return lastFrame ?? throw new InvalidOperationException($"The RPC call to '{route}' produced no response.");
    }
}

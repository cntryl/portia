using Cntryl.Fitz.Abstractions.Domains.Rpc;

namespace Cntryl.Portia;

/// <summary>
/// Sends requests to an out-of-process handler over Fitz RPC.
/// </summary>
/// <remarks>
/// This deliberately imposes no timeout of its own on <see cref="IRpcClient.CallAsync" /> — how
/// (or whether) a call to a route with no registered worker fails is entirely Fitz's contract to
/// honor, not Portia's to second-guess with an opinionated default. If a caller needs to bound
/// how long it waits, pass a <see cref="CancellationToken" /> that cancels after however long is
/// appropriate for that call.
/// </remarks>
/// <param name="rpc">The Fitz RPC client.</param>
/// <param name="serializer">The request and outcome serializer.</param>
public sealed class FitzRemoteRequestSender(IRpcClient rpc, IRequestSerializer serializer) : IRemoteRequestSender
{
    readonly IRpcClient _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
    readonly IRequestSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <inheritdoc />
    public async ValueTask<Result> SendAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest, ICallable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        var route = FitzRouting.ResolveRpcRoute(request, routeValues);
        var body = _serializer.Serialize(request, actorToken);
        var receivedFrame = await CallAsync(route, body, ct).ConfigureAwait(false);

        return _serializer.DeserializeOutcome(receivedFrame.Body);
    }

    /// <inheritdoc />
    public async ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        var route = FitzRouting.ResolveRpcRoute(request, routeValues);
        var body = _serializer.Serialize(request, actorToken);
        var receivedFrame = await CallAsync(route, body, ct).ConfigureAwait(false);

        return _serializer.DeserializeResult<TOut>(receivedFrame.Body);
    }

    async ValueTask<RpcResponseFrame> CallAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        RpcResponseFrame? lastFrame = null;

        await foreach (var frame in _rpc.CallAsync(route, body, ct).WithCancellation(ct).ConfigureAwait(false))
            lastFrame = frame;

        return lastFrame ?? throw new InvalidOperationException($"The RPC call to '{route}' produced no response.");
    }
}

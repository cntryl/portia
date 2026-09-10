using Cntryl.Fitz.Abstractions.Domains.Rpc;

namespace Cntryl.Portia;

/// <summary>
///     Sends requests to an out-of-process handler over Fitz RPC.
/// </summary>
/// <remarks>
///     This deliberately imposes no timeout of its own on <see cref="IRpcClient.CallAsync" /> — how
///     (or whether) a call to a route with no registered worker fails is entirely Fitz's contract to
///     honor, not Portia's to second-guess with an opinionated default. Confirmed against a real
///     broker (see <c>FitzBrokerIntegrationTests.ShouldFailFastWhenNoWorkerIsRegisteredForRoute</c>):
///     Fitz itself fails a call to an unregistered route in milliseconds, not by hanging, so this
///     isn't a gap left open on faith. If a caller needs to bound how long it waits regardless, pass
///     a <see cref="CancellationToken" /> that cancels after however long is appropriate for that call.
/// </remarks>
/// <param name="rpc">The Fitz RPC client.</param>
/// <param name="requestSerializer">Serializes outbound requests.</param>
/// <param name="outcomeDeserializer">Deserializes inbound outcomes.</param>
/// <param name="catalog">Provides generated request routes.</param>
public sealed class FitzRemoteRequestSender(
    IRpcClient rpc,
    IRequestSerializer requestSerializer,
    IRequestOutcomeDeserializer outcomeDeserializer,
    RequestTransportCatalog? catalog = null) : IRemoteRequestSender
{
    readonly RequestTransportCatalog _catalog = catalog ?? (requestSerializer as JsonRequestSerializer)?.Catalog
        ?? throw new ArgumentNullException(nameof(catalog));

    readonly IRequestOutcomeDeserializer _outcomeDeserializer =
        outcomeDeserializer ?? throw new ArgumentNullException(nameof(outcomeDeserializer));

    readonly IRequestSerializer _requestSerializer =
        requestSerializer ?? throw new ArgumentNullException(nameof(requestSerializer));

    readonly IRpcClient _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));

    /// <inheritdoc />
    public ValueTask<Result> SendAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        CancellationToken ct = default)
        where TRequest : IRequest, ICallable
        => SendAsync(request, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <inheritdoc />
    public async ValueTask<Result> SendAsync<TRequest>(TRequest request, RequestRouteValues routeValues,
        string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest, ICallable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        using var activity = PortiaTelemetry.StartSend(typeof(TRequest).Name, "fitz.rpc");
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "success";
        try
        {
            var route = FitzRouting.ResolveRpcRoute(_catalog, request, routeValues);
            var body = _requestSerializer.Serialize(request, actorToken, metadata,
                PortiaTelemetry.CaptureTraceContext());
            var result =
                _outcomeDeserializer.DeserializeOutcome((await CallAsync(route, body, ct).ConfigureAwait(false)).Body);
            outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        catch
        {
            outcome = "fault";
            throw;
        }
        finally
        {
            PortiaTelemetry.TransportFinished(started, "fitz.rpc", "send", outcome);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(TRequest request, RequestRouteValues routeValues,
        string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
        => SendAsync<TRequest, TOut>(request, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <inheritdoc />
    public async ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(TRequest request, RequestRouteValues routeValues,
        string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        using var activity = PortiaTelemetry.StartSend(typeof(TRequest).Name, "fitz.rpc");
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "success";
        try
        {
            var route = FitzRouting.ResolveRpcRoute(_catalog, request, routeValues);
            var body = _requestSerializer.Serialize(request, actorToken, metadata,
                PortiaTelemetry.CaptureTraceContext());
            var result =
                _outcomeDeserializer.DeserializeResult<TOut>((await CallAsync(route, body, ct).ConfigureAwait(false))
                    .Body);
            outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        catch
        {
            outcome = "fault";
            throw;
        }
        finally
        {
            PortiaTelemetry.TransportFinished(started, "fitz.rpc", "send", outcome);
        }
    }

    async ValueTask<RpcResponseFrame> CallAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        RpcResponseFrame? lastFrame = null;

        await foreach (var frame in _rpc.CallAsync(route, body, ct).WithCancellation(ct).ConfigureAwait(false))
            lastFrame = frame;

        return lastFrame ?? throw new InvalidOperationException($"The RPC call to '{route}' produced no response.");
    }
}

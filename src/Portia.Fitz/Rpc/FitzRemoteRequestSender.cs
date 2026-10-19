namespace Cntryl.Portia;

/// <summary>
///     Sends requests to an out-of-process handler over Fitz RPC.
/// </summary>
/// <remarks>
///     This deliberately imposes no timeout of its own on <see cref="IRpcClient.CallAsync(string, ReadOnlyMemory{byte}, CancellationToken)" /> — how
///     (or whether) a call to a route with no registered worker fails is entirely Fitz's contract to
///     honor, not Portia's to second-guess with an opinionated default. Confirmed against a real
///     broker (see <c>FitzBrokerIntegrationTests.ShouldFailFastWhenNoWorkerIsRegisteredForRoute</c>):
///     Fitz itself fails a call to an unregistered route in milliseconds, not by hanging, so this
///     isn't a gap left open on faith. If a caller needs to bound how long it waits regardless, pass
///     a <see cref="CancellationToken" /> that cancels after however long is appropriate for that call.
///     Fitz forwards cancellation to the remote worker on a best-effort basis. Ending the caller's
///     wait does not guarantee that the remote handler has stopped or that its effects were rolled
///     back; handlers must cooperate with cancellation and callers must reconcile uncertain outcomes.
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

        var requestName = _catalog.Get(request.GetType()).Discriminator.Name;
        using var activity = PortiaTelemetry.StartSend(requestName, "rpc", "fitz");
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
            PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = "canceled";
            PortiaTelemetry.RecordCanceled(activity);
            throw;
        }
        catch (Exception ex)
        {
            outcome = "fault";
            PortiaTelemetry.RecordFault(activity, ex);
            throw;
        }
        finally
        {
            PortiaTelemetry.TransportFinished(started, "rpc", "send", outcome);
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

        var requestName = _catalog.Get(request.GetType()).Discriminator.Name;
        using var activity = PortiaTelemetry.StartSend(requestName, "rpc", "fitz");
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
            PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = "canceled";
            PortiaTelemetry.RecordCanceled(activity);
            throw;
        }
        catch (Exception ex)
        {
            outcome = "fault";
            PortiaTelemetry.RecordFault(activity, ex);
            throw;
        }
        finally
        {
            PortiaTelemetry.TransportFinished(started, "rpc", "send", outcome);
        }
    }

#pragma warning disable RS0026 // The public methods intentionally mirror the sender's convenience and metadata overloads.
    /// <inheritdoc />
    public ValueTask<RemoteRequestOutcome> SendWithOutcomeAsync<TRequest>(TRequest request,
        RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest, ICallable
        => SendWithOutcomeAsync(request, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <inheritdoc />
    public async ValueTask<RemoteRequestOutcome> SendWithOutcomeAsync<TRequest>(TRequest request,
        RequestRouteValues routeValues, string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest, ICallable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);
        ArgumentNullException.ThrowIfNull(metadata);

        var requestName = _catalog.Get(request.GetType()).Discriminator.Name;
        using var activity = PortiaTelemetry.StartSend(requestName, "rpc", "fitz");
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "success";
        try
        {
            if (ct.IsCancellationRequested)
            {
                outcome = "canceled";
                PortiaTelemetry.RecordCanceled(activity);
                return new RemoteRequestOutcome.NotDispatched();
            }

            var route = FitzRouting.ResolveRpcRoute(_catalog, request, routeValues);
            var body = _requestSerializer.Serialize(request, actorToken, metadata,
                PortiaTelemetry.CaptureTraceContext());
            var call = await CallWithOutcomeAsync(route, body, ct).ConfigureAwait(false);
            if (call.Response is not { } response)
            {
                outcome = call.Exception is OperationCanceledException ? "canceled" : "fault";
                if (call.Exception is OperationCanceledException)
                    PortiaTelemetry.RecordCanceled(activity);
                else if (call.Exception is { } exception)
                    PortiaTelemetry.RecordFault(activity, exception);
                return CreateOutcome(call.Outcome!.Value);
            }

            var result = _outcomeDeserializer.DeserializeOutcome(response.Body);
            outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
            return new RemoteRequestOutcome.Completed(result);
        }
        catch (Exception ex)
        {
            outcome = "fault";
            PortiaTelemetry.RecordFault(activity, ex);
            throw;
        }
        finally
        {
            PortiaTelemetry.TransportFinished(started, "rpc", "send", outcome);
        }
    }

    /// <inheritdoc />
    public ValueTask<RemoteRequestOutcome<TOut>> SendWithOutcomeAsync<TRequest, TOut>(TRequest request,
        RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
        => SendWithOutcomeAsync<TRequest, TOut>(request, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <inheritdoc />
    public async ValueTask<RemoteRequestOutcome<TOut>> SendWithOutcomeAsync<TRequest, TOut>(TRequest request,
        RequestRouteValues routeValues, string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);
        ArgumentNullException.ThrowIfNull(metadata);

        var requestName = _catalog.Get(request.GetType()).Discriminator.Name;
        using var activity = PortiaTelemetry.StartSend(requestName, "rpc", "fitz");
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "success";
        try
        {
            if (ct.IsCancellationRequested)
            {
                outcome = "canceled";
                PortiaTelemetry.RecordCanceled(activity);
                return new RemoteRequestOutcome<TOut>.NotDispatched();
            }

            var route = FitzRouting.ResolveRpcRoute(_catalog, request, routeValues);
            var body = _requestSerializer.Serialize(request, actorToken, metadata,
                PortiaTelemetry.CaptureTraceContext());
            var call = await CallWithOutcomeAsync(route, body, ct).ConfigureAwait(false);
            if (call.Response is not { } response)
            {
                outcome = call.Exception is OperationCanceledException ? "canceled" : "fault";
                if (call.Exception is OperationCanceledException)
                    PortiaTelemetry.RecordCanceled(activity);
                else if (call.Exception is { } exception)
                    PortiaTelemetry.RecordFault(activity, exception);
                return CreateOutcome<TOut>(call.Outcome!.Value);
            }

            var result = _outcomeDeserializer.DeserializeResult<TOut>(response.Body);
            outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
            return new RemoteRequestOutcome<TOut>.Completed(result);
        }
        catch (Exception ex)
        {
            outcome = "fault";
            PortiaTelemetry.RecordFault(activity, ex);
            throw;
        }
        finally
        {
            PortiaTelemetry.TransportFinished(started, "rpc", "send", outcome);
        }
    }
#pragma warning restore RS0026

    async ValueTask<FitzRpcCallOutcome> CallWithOutcomeAsync(string route, ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        var call = _rpc.CallAsync(route, body, ct);
        RpcResponseFrame? lastFrame = null;
        try
        {
            await foreach (var frame in call.WithCancellation(ct).ConfigureAwait(false))
                lastFrame = frame;
        }
        catch (OperationCanceledException exception) when (ct.IsCancellationRequested)
        {
            if (lastFrame is { } response)
                return new FitzRpcCallOutcome(response, null, null);

            var cancellation = await TryGetCancellationOutcomeAsync(call).ConfigureAwait(false);
            return new FitzRpcCallOutcome(null, cancellation is { } observed
                ? MapCancellationOutcome(observed)
                : RemoteRequestOutcomeKind.OutcomeUnknown, exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (lastFrame is { } response)
                return new FitzRpcCallOutcome(response, null, null);

            var cancellation = await TryGetCancellationOutcomeAsync(call).ConfigureAwait(false);
            return new FitzRpcCallOutcome(null, cancellation is { } observed
                ? MapCancellationOutcome(observed)
                : RemoteRequestOutcomeKind.OutcomeUnknown, exception);
        }

        if (lastFrame is { } completedResponse)
            return new FitzRpcCallOutcome(completedResponse, null, null);

        var finalCancellation = await TryGetCancellationOutcomeAsync(call).ConfigureAwait(false);
        return new FitzRpcCallOutcome(null, finalCancellation is { } outcome
            ? MapCancellationOutcome(outcome)
            : RemoteRequestOutcomeKind.OutcomeUnknown,
            new InvalidOperationException("The RPC call produced no final response."));
    }

    static async ValueTask<RpcCancellationOutcome?> TryGetCancellationOutcomeAsync(RpcCall call)
    {
        try
        {
            return await call.Cancellation.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    static RemoteRequestOutcomeKind MapCancellationOutcome(RpcCancellationOutcome cancellation) => cancellation switch
    {
        RpcCancellationOutcome.RequestNotSent or RpcCancellationOutcome.QueuedRemoved =>
            RemoteRequestOutcomeKind.NotDispatched,
        RpcCancellationOutcome.Forwarded => RemoteRequestOutcomeKind.CancellationForwarded,
        _ => RemoteRequestOutcomeKind.OutcomeUnknown
    };

    static RemoteRequestOutcome CreateOutcome(RemoteRequestOutcomeKind kind) => kind switch
    {
        RemoteRequestOutcomeKind.NotDispatched => new RemoteRequestOutcome.NotDispatched(),
        RemoteRequestOutcomeKind.CancellationForwarded => new RemoteRequestOutcome.CancellationForwarded(),
        _ => new RemoteRequestOutcome.OutcomeUnknown()
    };

    static RemoteRequestOutcome<TOut> CreateOutcome<TOut>(RemoteRequestOutcomeKind kind) => kind switch
    {
        RemoteRequestOutcomeKind.NotDispatched => new RemoteRequestOutcome<TOut>.NotDispatched(),
        RemoteRequestOutcomeKind.CancellationForwarded =>
            new RemoteRequestOutcome<TOut>.CancellationForwarded(),
        _ => new RemoteRequestOutcome<TOut>.OutcomeUnknown()
    };

    readonly record struct FitzRpcCallOutcome(
        RpcResponseFrame? Response,
        RemoteRequestOutcomeKind? Outcome,
        Exception? Exception);

    async ValueTask<RpcResponseFrame> CallAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        RpcResponseFrame? lastFrame = null;

        await foreach (var frame in _rpc.CallAsync(route, body, ct).WithCancellation(ct).ConfigureAwait(false))
            lastFrame = frame;

        return lastFrame ?? throw new InvalidOperationException("The RPC call produced no response.");
    }
}

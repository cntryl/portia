using Cntryl.Fitz.Abstractions.Domains.Rpc;

namespace Cntryl.Portia;

/// <summary>
/// Receives requests over Fitz RPC and dispatches them through the same <see cref="IRequestBus" />
/// as any other origin — the other half of <see cref="FitzRemoteRequestSender" />. Registration is
/// per request type (mirroring how a handler is registered once per request type), since the
/// result type — needed to serialize the outcome correctly — has to be known at registration time,
/// not recovered from an arbitrary incoming byte payload at dispatch time.
/// </summary>
/// <param name="rpc">The Fitz RPC client.</param>
/// <param name="serializer">The request and outcome serializer.</param>
/// <param name="bus">The request bus every registered worker dispatches through.</param>
/// <param name="actorValidator">Re-validates each request's carried actor token — signature and
/// expiry included — at the moment it's actually received, not just at the moment it was sent.</param>
public sealed class FitzRpcRequestServer(
    IRpcClient rpc,
    IRequestSerializer serializer,
    IRequestBus bus,
    IRequestActorValidator actorValidator)
{
    readonly IRpcClient _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
    readonly IRequestSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    readonly IRequestBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    readonly IRequestActorValidator _actorValidator = actorValidator ?? throw new ArgumentNullException(nameof(actorValidator));

    /// <summary>
    /// Registers a worker for a no-result request, at the pattern its own
    /// <see cref="RequestRouteAttribute" /> declares.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A registration that can later unregister the worker.</returns>
    public ValueTask<RpcWorkerRegistration> RegisterAsync<TRequest>(CancellationToken ct = default)
        where TRequest : IRequest, ICallable
    {
        var pattern = FitzRouting.ResolveRpcWorkerPattern<TRequest>();

        return new ValueTask<RpcWorkerRegistration>(_rpc.RegisterWorkerAsync(
            pattern,
            async (request, writer, handlerCt) =>
            {
                var (deserialized, actorToken) = _serializer.DeserializeRequest(request.Body);

                if (deserialized is not TRequest typed)
                {
                    await writer.SendAsync(
                        _serializer.SerializeOutcome(Result.Failure(new RequestError(
                            RequestErrorKind.Validation,
                            $"Expected a '{typeof(TRequest)}' payload."))),
                        true,
                        handlerCt).ConfigureAwait(false);
                    return;
                }

                if (await _actorValidator.ValidateAsync(actorToken, handlerCt).ConfigureAwait(false) is not { IsSuccess: true, Value: { } actor })
                {
                    await writer.SendAsync(
                        _serializer.SerializeOutcome(Result.Failure(new RequestError(RequestErrorKind.Unauthorized, "Actor token failed validation."))),
                        true,
                        handlerCt).ConfigureAwait(false);
                    return;
                }

                var result = await _bus.SendAsync(typed, actor, handlerCt).ConfigureAwait(false);
                await writer.SendAsync(_serializer.SerializeOutcome(result), true, handlerCt).ConfigureAwait(false);
            },
            ct: ct));
    }

    /// <summary>
    /// Registers a worker for a request that produces a result, at the pattern its own
    /// <see cref="RequestRouteAttribute" /> declares.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A registration that can later unregister the worker.</returns>
    public ValueTask<RpcWorkerRegistration> RegisterAsync<TRequest, TOut>(CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
    {
        var pattern = FitzRouting.ResolveRpcWorkerPattern<TRequest>();

        return new ValueTask<RpcWorkerRegistration>(_rpc.RegisterWorkerAsync(
            pattern,
            async (request, writer, handlerCt) =>
            {
                var (deserialized, actorToken) = _serializer.DeserializeRequest(request.Body);

                if (deserialized is not TRequest typed)
                {
                    await writer.SendAsync(
                        _serializer.SerializeResult(Result<TOut>.Failure(new RequestError(
                            RequestErrorKind.Validation,
                            $"Expected a '{typeof(TRequest)}' payload."))),
                        true,
                        handlerCt).ConfigureAwait(false);
                    return;
                }

                if (await _actorValidator.ValidateAsync(actorToken, handlerCt).ConfigureAwait(false) is not { IsSuccess: true, Value: { } actor })
                {
                    await writer.SendAsync(
                        _serializer.SerializeResult(Result<TOut>.Failure(new RequestError(RequestErrorKind.Unauthorized, "Actor token failed validation."))),
                        true,
                        handlerCt).ConfigureAwait(false);
                    return;
                }

                var result = await _bus.SendAsync(typed, actor, handlerCt).ConfigureAwait(false);
                await writer.SendAsync(_serializer.SerializeResult(result), true, handlerCt).ConfigureAwait(false);
            },
            ct: ct));
    }
}

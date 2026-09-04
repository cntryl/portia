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
/// <param name="requestDeserializer">Deserializes inbound requests.</param>
/// <param name="outcomeSerializer">Serializes outbound outcomes.</param>
/// <param name="bus">The request bus every registered worker dispatches through.</param>
/// <param name="actorValidator">Re-validates each request's carried actor token — signature and
/// expiry included — at the moment it's actually received, not just at the moment it was sent.</param>
public sealed class FitzRpcRequestServer(
    IRpcClient rpc,
    IRequestDeserializer requestDeserializer,
    IRequestOutcomeSerializer outcomeSerializer,
    IRequestBus bus,
    IRequestActorValidator actorValidator)
{
    readonly IRpcClient _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
    readonly IRequestDeserializer _requestDeserializer = requestDeserializer ?? throw new ArgumentNullException(nameof(requestDeserializer));
    readonly IRequestOutcomeSerializer _outcomeSerializer = outcomeSerializer ?? throw new ArgumentNullException(nameof(outcomeSerializer));
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
                var (deserialized, actorToken) = _requestDeserializer.DeserializeRequest(request.Body);

                if (deserialized is not TRequest typed)
                {
                    await writer.SendAsync(
                        _outcomeSerializer.SerializeOutcome(Result.Failure(new RequestError(
                            RequestErrorKind.Validation,
                            $"Expected a '{typeof(TRequest)}' payload."))),
                        true,
                        handlerCt).ConfigureAwait(false);
                    return;
                }

                var dispatch = await RequestDispatch.SendAsync(
                    _actorValidator, _bus, typed, actorToken, handlerCt).ConfigureAwait(false);
                await writer.SendAsync(_outcomeSerializer.SerializeOutcome(dispatch.Outcome), true, handlerCt).ConfigureAwait(false);
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
                var (deserialized, actorToken) = _requestDeserializer.DeserializeRequest(request.Body);

                if (deserialized is not TRequest typed)
                {
                    await writer.SendAsync(
                        _outcomeSerializer.SerializeResult(Result<TOut>.Failure(new RequestError(
                            RequestErrorKind.Validation,
                            $"Expected a '{typeof(TRequest)}' payload."))),
                        true,
                        handlerCt).ConfigureAwait(false);
                    return;
                }

                var dispatch = await RequestDispatch.SendAsync(
                    _actorValidator, _bus, typed, actorToken, handlerCt).ConfigureAwait(false);
                await writer.SendAsync(_outcomeSerializer.SerializeResult(dispatch.Outcome), true, handlerCt).ConfigureAwait(false);
            },
            ct: ct));
    }
}

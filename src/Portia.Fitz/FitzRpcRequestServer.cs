using Cntryl.Fitz.Abstractions.Domains.Rpc;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
/// Receives requests over Fitz RPC and dispatches them through the same <see cref="IRequestBus" />
/// as any other origin — the other half of <see cref="FitzRemoteRequestSender" />. Registration is
/// per request type (mirroring how a handler is registered once per request type), since the
/// result type — needed to serialize the outcome correctly — has to be known at registration time,
/// not recovered from an arbitrary incoming byte payload at dispatch time.
/// </summary>
/// <param name="rpc">The Fitz RPC client.</param>
/// <param name="scopeFactory">Owns application dependencies for each RPC invocation.</param>
/// <param name="catalog">Provides generated request routes.</param>
public sealed class FitzRpcRequestServer(IRpcClient rpc, IServiceScopeFactory scopeFactory, RequestTransportCatalog? catalog = null) : IRequestRpcRegistrar
{
    readonly IRpcClient _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
    readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    RequestTransportCatalog? _catalog = catalog;

    RequestTransportCatalog Catalog()
    {
        if (_catalog is not null)
            return _catalog;
        using var scope = _scopeFactory.CreateScope();
        return _catalog = new RequestTransportCatalog(scope.ServiceProvider.GetServices<RequestTransportRegistration>());
    }

    /// <summary>Registers callable descriptors for explicitly registered Portia requests.</summary>
    /// <param name="ct">Cancels registration.</param>
    /// <returns>Owns all worker registrations; dispose during host shutdown.</returns>
    public async ValueTask<IAsyncDisposable> RegisterRequestsAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var workers = new Workers();
        try
        {
            var handled = scope.ServiceProvider.GetServices<RequestHandlerRegistration>()
                .Select(registration => registration.RequestType)
                .ToHashSet();
            foreach (var descriptor in scope.ServiceProvider.GetServices<RequestTransportRegistration>())
            {
                if (!handled.Contains(descriptor.RequestType) || !descriptor.Transports.HasFlag(RequestTransports.Callable))
                    continue;
                var register = descriptor.RegisterRpc ?? throw new InvalidOperationException(
                    $"Callable request '{descriptor.RequestType}' has no generated RPC registration.");
                workers.Items.Add(await register(this, ct).ConfigureAwait(false));
            }
            return workers;
        }
        catch
        {
            try { await workers.DisposeAsync().ConfigureAwait(false); }
            catch { /* Preserve the registration failure after attempting every cleanup. */ }
            throw;
        }
    }

    async ValueTask<IAsyncDisposable> IRequestRpcRegistrar.RegisterAsync<TRequest>(CancellationToken ct)
        => await RegisterAsync<TRequest>(ct).ConfigureAwait(false);

    async ValueTask<IAsyncDisposable> IRequestRpcRegistrar.RegisterAsync<TRequest, TOut>(CancellationToken ct)
        => await RegisterAsync<TRequest, TOut>(ct).ConfigureAwait(false);

    sealed class Workers : IAsyncDisposable
    {
        public List<IAsyncDisposable> Items { get; } = [];

        public async ValueTask DisposeAsync()
        {
            List<Exception>? errors = null;
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                try { await Items[i].DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            Items.Clear();
            if (errors is not null)
                throw new AggregateException(errors);
        }
    }

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
        var pattern = FitzRouting.ResolveRpcWorkerPattern<TRequest>(Catalog());

        return new ValueTask<RpcWorkerRegistration>(_rpc.RegisterWorkerAsync(
            pattern,
            async (request, writer, handlerCt) =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var requestDeserializer = scope.ServiceProvider.GetRequiredService<IRequestDeserializer>();
                var outcomeSerializer = scope.ServiceProvider.GetRequiredService<IRequestOutcomeSerializer>();
                var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                var actorValidator = scope.ServiceProvider.GetRequiredService<IRequestActorValidator>();
                var envelope = requestDeserializer.DeserializeEnvelope(request.Body);

                if (envelope.Request is not TRequest typed)
                {
                    await writer.SendAsync(
                        outcomeSerializer.SerializeOutcome(Result.Failure(new RequestError(
                            RequestErrorKind.Validation,
                            $"Expected a '{typeof(TRequest)}' payload."))),
                        true,
                        handlerCt).ConfigureAwait(false);
                    return;
                }

                var dispatch = await RequestDispatch.SendAsync(actorValidator, bus, typed,
                    envelope.ToDelivery(new RpcInvocation(request.Route), scope.ServiceProvider.GetService<TimeProvider>()),
                    handlerCt).ConfigureAwait(false);
                await writer.SendAsync(outcomeSerializer.SerializeOutcome(dispatch.Outcome), true, handlerCt).ConfigureAwait(false);
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
        var pattern = FitzRouting.ResolveRpcWorkerPattern<TRequest>(Catalog());

        return new ValueTask<RpcWorkerRegistration>(_rpc.RegisterWorkerAsync(
            pattern,
            async (request, writer, handlerCt) =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var requestDeserializer = scope.ServiceProvider.GetRequiredService<IRequestDeserializer>();
                var outcomeSerializer = scope.ServiceProvider.GetRequiredService<IRequestOutcomeSerializer>();
                var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                var actorValidator = scope.ServiceProvider.GetRequiredService<IRequestActorValidator>();
                var envelope = requestDeserializer.DeserializeEnvelope(request.Body);

                if (envelope.Request is not TRequest typed)
                {
                    await writer.SendAsync(
                        outcomeSerializer.SerializeResult(Result<TOut>.Failure(new RequestError(
                            RequestErrorKind.Validation,
                            $"Expected a '{typeof(TRequest)}' payload."))),
                        true,
                        handlerCt).ConfigureAwait(false);
                    return;
                }

                var dispatch = await RequestDispatch.SendAsync(actorValidator, bus, typed,
                    envelope.ToDelivery(new RpcInvocation(request.Route), scope.ServiceProvider.GetService<TimeProvider>()),
                    handlerCt).ConfigureAwait(false);
                await writer.SendAsync(outcomeSerializer.SerializeResult(dispatch.Outcome), true, handlerCt).ConfigureAwait(false);
            },
            ct: ct));
    }
}

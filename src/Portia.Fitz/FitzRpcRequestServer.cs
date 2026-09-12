using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Receives requests over Fitz RPC and dispatches them through the same <see cref="IRequestBus" />
///     as any other origin — the other half of <see cref="FitzRemoteRequestSender" />. Registration is
///     per request type (mirroring how a handler is registered once per request type), since the
///     result type — needed to serialize the outcome correctly — has to be known at registration time,
///     not recovered from an arbitrary incoming byte payload at dispatch time.
/// </summary>
/// <param name="rpc">The Fitz RPC client.</param>
/// <param name="scopeFactory">Owns application dependencies for each RPC invocation.</param>
/// <param name="catalog">Provides generated request routes.</param>
public sealed class FitzRpcRequestServer(
    IRpcClient rpc,
    IServiceScopeFactory scopeFactory,
    RequestTransportCatalog? catalog = null) : IRequestRpcRegistrar
{
    // Resolving the fallback catalog opens a scope and re-reads every generated descriptor, so it
    // happens once for the life of the server even when two registrations race.
    readonly Lazy<RequestTransportCatalog> _catalog = new(() => catalog ?? Compose(scopeFactory),
        LazyThreadSafetyMode.ExecutionAndPublication);

    readonly IRpcClient _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
    readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    async ValueTask<IAsyncDisposable> IRequestRpcRegistrar.RegisterAsync<TRequest>(CancellationToken ct)
        => await RegisterAsync<TRequest>(ct).ConfigureAwait(false);

    async ValueTask<IAsyncDisposable> IRequestRpcRegistrar.RegisterAsync<TRequest, TOut>(CancellationToken ct)
        => await RegisterAsync<TRequest, TOut>(ct).ConfigureAwait(false);

    RequestTransportCatalog Catalog() => _catalog.Value;

    static RequestTransportCatalog Compose(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        return new RequestTransportCatalog(scope.ServiceProvider.GetServices<RequestTransportRegistration>());
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
                if (!handled.Contains(descriptor.RequestType) ||
                    !descriptor.Transports.HasFlag(RequestTransports.Callable))
                {
                    continue;
                }

                var register = descriptor.RegisterRpc ?? throw new InvalidOperationException(
                    $"Callable request '{descriptor.RequestType}' has no generated RPC registration.");
                workers.Items.Add(await register(this, ct).ConfigureAwait(false));
            }

            return workers;
        }
        catch
        {
            try
            {
                await workers.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                /* Preserve the registration failure after attempting every cleanup. */
            }

            throw;
        }
    }

    /// <summary>
    ///     Registers a worker for a no-result request, at the pattern its own
    ///     <see cref="RequestRouteAttribute" /> declares.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A registration that can later unregister the worker.</returns>
    public ValueTask<RpcWorkerRegistration> RegisterAsync<TRequest>(CancellationToken ct = default)
        where TRequest : IRequest, ICallable
    {
        var pattern = FitzRouting.ResolveRpcWorkerPattern<TRequest>(Catalog());
        var requestName = Catalog().Get(typeof(TRequest)).Discriminator.Name;

        return new ValueTask<RpcWorkerRegistration>(_rpc.RegisterWorkerAsync(
            pattern,
            async (request, writer, handlerCt) =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var requestDeserializer = scope.ServiceProvider.GetRequiredService<IRequestDeserializer>();
                var outcomeSerializer = scope.ServiceProvider.GetRequiredService<IRequestOutcomeSerializer>();
                var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                var actorValidator = scope.ServiceProvider.GetRequiredService<IRequestActorValidator>();
                var invocation = new RpcInvocation(request.Route) { MessagingSystem = "fitz" };
                DeserializedRequest envelope;
                try
                {
                    envelope = requestDeserializer.DeserializeEnvelope(request.Body);
                }
                catch (OperationCanceledException) when (handlerCt.IsCancellationRequested)
                {
                    using var process = PortiaTelemetry.StartProcess(requestName, invocation, null);
                    PortiaTelemetry.RecordCanceled(process);
                    PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Canceled);
                    throw;
                }
                catch (Exception ex)
                {
                    using var process = PortiaTelemetry.StartProcess(requestName, invocation, null);
                    PortiaTelemetry.RecordFault(process, ex);
                    PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Fault);
                    throw;
                }

                if (envelope.Request is not TRequest typed)
                {
                    var failure = new RequestError(RequestErrorKind.Validation, "Unexpected request contract.");
                    using var process = PortiaTelemetry.StartProcess(requestName, invocation, envelope.TraceContext);
                    try
                    {
                        await writer.SendAsync(outcomeSerializer.SerializeOutcome(Result.Failure(failure)), true,
                            handlerCt).ConfigureAwait(false);
                        PortiaTelemetry.RecordOutcome(process, false, failure);
                        PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Completed);
                    }
                    catch (OperationCanceledException) when (handlerCt.IsCancellationRequested)
                    {
                        PortiaTelemetry.RecordCanceled(process);
                        PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Canceled);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        PortiaTelemetry.RecordFault(process, ex);
                        PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Fault);
                        throw;
                    }

                    return;
                }

                try
                {
                    _ = await RequestDispatch.SendAsync(actorValidator, bus, typed,
                            envelope.ToDelivery(invocation, scope.ServiceProvider.GetService<TimeProvider>()),
                            async (outcome, token) =>
                            {
                                await writer.SendAsync(outcomeSerializer.SerializeOutcome(outcome), true, token)
                                    .ConfigureAwait(false);
                            }, handlerCt)
                        .ConfigureAwait(false);
                    PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Completed);
                }
                catch (OperationCanceledException) when (handlerCt.IsCancellationRequested)
                {
                    PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Canceled);
                    throw;
                }
                catch
                {
                    PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Fault);
                    throw;
                }
            },
            ct: ct));
    }

    /// <summary>
    ///     Registers a worker for a request that produces a result, at the pattern its own
    ///     <see cref="RequestRouteAttribute" /> declares.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A registration that can later unregister the worker.</returns>
    public ValueTask<RpcWorkerRegistration> RegisterAsync<TRequest, TOut>(CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
    {
        var pattern = FitzRouting.ResolveRpcWorkerPattern<TRequest>(Catalog());
        var requestName = Catalog().Get(typeof(TRequest)).Discriminator.Name;

        return new ValueTask<RpcWorkerRegistration>(_rpc.RegisterWorkerAsync(
            pattern,
            async (request, writer, handlerCt) =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var requestDeserializer = scope.ServiceProvider.GetRequiredService<IRequestDeserializer>();
                var outcomeSerializer = scope.ServiceProvider.GetRequiredService<IRequestOutcomeSerializer>();
                var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                var actorValidator = scope.ServiceProvider.GetRequiredService<IRequestActorValidator>();
                var invocation = new RpcInvocation(request.Route) { MessagingSystem = "fitz" };
                DeserializedRequest envelope;
                try
                {
                    envelope = requestDeserializer.DeserializeEnvelope(request.Body);
                }
                catch (OperationCanceledException) when (handlerCt.IsCancellationRequested)
                {
                    using var process = PortiaTelemetry.StartProcess(requestName, invocation, null);
                    PortiaTelemetry.RecordCanceled(process);
                    PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Canceled);
                    throw;
                }
                catch (Exception ex)
                {
                    using var process = PortiaTelemetry.StartProcess(requestName, invocation, null);
                    PortiaTelemetry.RecordFault(process, ex);
                    PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Fault);
                    throw;
                }

                if (envelope.Request is not TRequest typed)
                {
                    var failure = new RequestError(RequestErrorKind.Validation, "Unexpected request contract.");
                    using var process = PortiaTelemetry.StartProcess(requestName, invocation, envelope.TraceContext);
                    try
                    {
                        await writer.SendAsync(outcomeSerializer.SerializeResult(Result<TOut>.Failure(failure)), true,
                            handlerCt).ConfigureAwait(false);
                        PortiaTelemetry.RecordOutcome(process, false, failure);
                        PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Completed);
                    }
                    catch (OperationCanceledException) when (handlerCt.IsCancellationRequested)
                    {
                        PortiaTelemetry.RecordCanceled(process);
                        PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Canceled);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        PortiaTelemetry.RecordFault(process, ex);
                        PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Fault);
                        throw;
                    }

                    return;
                }

                try
                {
                    _ = await RequestDispatch.SendAsync(actorValidator, bus, typed,
                            envelope.ToDelivery(invocation, scope.ServiceProvider.GetService<TimeProvider>()),
                            async (outcome, token) =>
                            {
                                await writer.SendAsync(outcomeSerializer.SerializeResult(outcome), true, token)
                                    .ConfigureAwait(false);
                            }, handlerCt)
                        .ConfigureAwait(false);
                    PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Completed);
                }
                catch (OperationCanceledException) when (handlerCt.IsCancellationRequested)
                {
                    PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Canceled);
                    throw;
                }
                catch
                {
                    PortiaTelemetry.RecordDelivery(requestName, "rpc", RequestDeliveryOutcome.Fault);
                    throw;
                }
            },
            ct: ct));
    }

    sealed class Workers : IAsyncDisposable
    {
        public List<IAsyncDisposable> Items { get; } = [];

        public async ValueTask DisposeAsync()
        {
            List<Exception>? errors = null;
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                try
                {
                    await Items[i].DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    (errors ??= []).Add(error);
                }
            }

            Items.Clear();
            if (errors is not null)
            {
                throw new AggregateException(errors);
            }
        }
    }
}

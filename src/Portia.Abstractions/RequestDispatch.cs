namespace Cntryl.Portia;

/// <summary>
/// The outcome of re-validating an actor token and conditionally dispatching a no-result request.
/// </summary>
/// <param name="Outcome">The validation failure or request-bus outcome.</param>
/// <param name="WasDispatched">Whether actor validation succeeded and the bus was invoked.</param>
public readonly record struct RequestDispatchOutcome(Result Outcome, bool WasDispatched);

/// <summary>
/// The outcome of re-validating an actor token and conditionally dispatching a result-bearing request.
/// </summary>
/// <typeparam name="TOut">The request result type.</typeparam>
/// <param name="Outcome">The validation failure or request-bus outcome.</param>
/// <param name="WasDispatched">Whether actor validation succeeded and the bus was invoked.</param>
public readonly record struct RequestDispatchOutcome<TOut>(Result<TOut> Outcome, bool WasDispatched);

/// <summary>
/// Shares actor re-validation and request-bus dispatch across every inbound transport while
/// keeping the validator and bus as explicit abstractions supplied by the adapter.
/// </summary>
public static class RequestDispatch
{
    /// <summary>
    /// Re-validates an actor token and dispatches a no-result request when validation succeeds.
    /// </summary>
    /// <param name="actorValidator">The transport-boundary actor-token validator.</param>
    /// <param name="bus">The request bus that executes validated requests.</param>
    /// <param name="request">The deserialized request.</param>
    /// <param name="actorToken">The opaque actor token received by the transport.</param>
    /// <param name="invocation">The concrete inbound transport facts.</param>
    /// <param name="metadata">The validated logical request identity.</param>
    /// <param name="timeProvider">The optional clock used by the request context.</param>
    /// <param name="ct">Cancels actor validation and dispatch.</param>
    /// <returns>The validation or dispatch outcome and whether the bus was invoked.</returns>
    /// <remarks>This compatibility overload does not supply propagated trace context.</remarks>
    public static ValueTask<RequestDispatchOutcome> SendAsync(
        IRequestActorValidator actorValidator,
        IRequestBus bus,
        IRequest request,
        string? actorToken,
        RequestInvocation invocation,
        RequestMetadata metadata,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default) =>
        SendAsync(actorValidator, bus, request, actorToken, invocation, metadata, timeProvider, traceContext: null, ct);

    /// <summary>Re-validates and dispatches a request with propagated W3C trace context.</summary>
    /// <param name="actorValidator">The transport-boundary actor-token validator.</param>
    /// <param name="bus">The request bus that executes validated requests.</param>
    /// <param name="request">The deserialized request.</param>
    /// <param name="actorToken">The opaque actor token received by the transport.</param>
    /// <param name="invocation">The concrete inbound transport facts.</param>
    /// <param name="metadata">The validated logical request identity.</param>
    /// <param name="timeProvider">The optional clock used by the request context.</param>
    /// <param name="traceContext">The optional W3C context received with the request.</param>
    /// <param name="ct">Cancels actor validation and dispatch.</param>
    /// <returns>The validation or dispatch outcome and whether the bus was invoked.</returns>
    /// <remarks>Creates one consumer activity. Scheduled invocations start a new trace linked to
    /// <paramref name="traceContext" />; other inbound invocations use it as their parent.</remarks>
    public static async ValueTask<RequestDispatchOutcome> SendAsync(
        IRequestActorValidator actorValidator,
        IRequestBus bus,
        IRequest request,
        string? actorToken,
        RequestInvocation invocation,
        RequestMetadata metadata,
        TimeProvider? timeProvider,
        RequestTraceContext? traceContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actorValidator);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(request);

        var transport = Transport(invocation);
        using var process = PortiaTelemetry.StartProcess(request.GetType().Name, transport, traceContext, invocation is ScheduleInvocation);
        var actorResult = await actorValidator.ValidateAsync(actorToken, ct).ConfigureAwait(false);

        return actorResult is not { IsSuccess: true, Value: { } actor }
            ? new RequestDispatchOutcome(Result.Failure(actorResult.Error!), WasDispatched: false)
            : new RequestDispatchOutcome(
                await bus.DispatchAsync(request, new RequestDispatchContext(actor, invocation, metadata, timeProvider), ct).ConfigureAwait(false),
                WasDispatched: true);
    }

    /// <summary>
    /// Re-validates an actor token and dispatches a result-bearing request when validation succeeds.
    /// </summary>
    /// <typeparam name="TOut">The successful result value type.</typeparam>
    /// <param name="actorValidator">The transport-boundary actor-token validator.</param>
    /// <param name="bus">The request bus that executes validated requests.</param>
    /// <param name="request">The deserialized result-bearing request.</param>
    /// <param name="actorToken">The opaque actor token received by the transport.</param>
    /// <param name="invocation">The concrete inbound transport facts.</param>
    /// <param name="metadata">The validated logical request identity.</param>
    /// <param name="timeProvider">The optional clock used by the request context.</param>
    /// <param name="ct">Cancels actor validation and dispatch.</param>
    /// <returns>The validation or dispatch outcome and whether the bus was invoked.</returns>
    /// <remarks>This compatibility overload does not supply propagated trace context.</remarks>
    public static ValueTask<RequestDispatchOutcome<TOut>> SendAsync<TOut>(
        IRequestActorValidator actorValidator,
        IRequestBus bus,
        IRequest<TOut> request,
        string? actorToken,
        RequestInvocation invocation,
        RequestMetadata metadata,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default) =>
        SendAsync(actorValidator, bus, request, actorToken, invocation, metadata, timeProvider, traceContext: null, ct);

    /// <summary>Re-validates and dispatches a result-bearing request with propagated W3C trace context.</summary>
    /// <typeparam name="TOut">The successful result value type.</typeparam>
    /// <param name="actorValidator">The transport-boundary actor-token validator.</param>
    /// <param name="bus">The request bus that executes validated requests.</param>
    /// <param name="request">The deserialized result-bearing request.</param>
    /// <param name="actorToken">The opaque actor token received by the transport.</param>
    /// <param name="invocation">The concrete inbound transport facts.</param>
    /// <param name="metadata">The validated logical request identity.</param>
    /// <param name="timeProvider">The optional clock used by the request context.</param>
    /// <param name="traceContext">The optional W3C context received with the request.</param>
    /// <param name="ct">Cancels actor validation and dispatch.</param>
    /// <returns>The validation or dispatch outcome and whether the bus was invoked.</returns>
    /// <remarks>Creates one consumer activity. Scheduled invocations start a new trace linked to
    /// <paramref name="traceContext" />; other inbound invocations use it as their parent.</remarks>
    public static async ValueTask<RequestDispatchOutcome<TOut>> SendAsync<TOut>(
        IRequestActorValidator actorValidator,
        IRequestBus bus,
        IRequest<TOut> request,
        string? actorToken,
        RequestInvocation invocation,
        RequestMetadata metadata,
        TimeProvider? timeProvider,
        RequestTraceContext? traceContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actorValidator);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(request);

        var transport = Transport(invocation);
        using var process = PortiaTelemetry.StartProcess(request.GetType().Name, transport, traceContext, invocation is ScheduleInvocation);
        var actorResult = await actorValidator.ValidateAsync(actorToken, ct).ConfigureAwait(false);

        return actorResult is not { IsSuccess: true, Value: { } actor }
            ? new RequestDispatchOutcome<TOut>(Result<TOut>.Failure(actorResult.Error!), WasDispatched: false)
            : new RequestDispatchOutcome<TOut>(
                await bus.DispatchAsync(request, new RequestDispatchContext(actor, invocation, metadata, timeProvider), ct).ConfigureAwait(false),
                WasDispatched: true);
    }

    static string Transport(RequestInvocation invocation) => invocation switch
    {
        RpcInvocation => "fitz.rpc",
        QueueInvocation => "fitz.queue",
        NoticeInvocation => "fitz.notice",
        ScheduleInvocation => "fitz.schedule",
        HttpInvocation => "http",
        _ => "local",
    };
}

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
/// Everything an inbound transport knows about one delivery apart from the request itself: who
/// claims to have sent it, the logical identity to preserve, the ingress facts, and the clock to
/// run it under. Grouping these keeps <see cref="RequestDispatch" />'s two entry points to four
/// parameters instead of nine, and gives an adapter one value to build rather than an argument
/// list to keep in the right order.
/// </summary>
/// <param name="Name">The request's stable wire name — its <see cref="DiscriminatorAttribute" />
/// name where the transport resolved one. This is what reaches telemetry, so it must not be
/// derived from a CLR type name that can be renamed without changing the contract.</param>
/// <param name="Invocation">The concrete inbound transport facts.</param>
/// <param name="Metadata">The validated logical request identity.</param>
/// <param name="ActorToken">The opaque actor token received by the transport, or
/// <see langword="null" /> for an unauthenticated delivery.</param>
/// <param name="TraceContext">The optional W3C context received with the request.</param>
/// <param name="TimeProvider">The optional clock used by the request context.</param>
public sealed record RequestDelivery(
    string Name,
    RequestInvocation Invocation,
    RequestMetadata Metadata,
    string? ActorToken = null,
    RequestTraceContext? TraceContext = null,
    TimeProvider? TimeProvider = null)
{
    /// <summary>
    /// Builds a delivery, falling back to the request's CLR type name when the transport did not
    /// resolve a wire name. The fallback lives here so every adapter degrades the same way, and
    /// so a request that genuinely has no declared discriminator (an in-process-only request that
    /// reached a transport) still reports something rather than nothing.
    /// </summary>
    /// <param name="request">The delivered request, used only for the name fallback.</param>
    /// <param name="name">The wire name the transport resolved, or <see langword="null" />.</param>
    /// <param name="invocation">The concrete inbound transport facts.</param>
    /// <param name="metadata">The validated logical request identity.</param>
    /// <param name="actorToken">The opaque actor token received by the transport.</param>
    /// <param name="traceContext">The optional W3C context received with the request.</param>
    /// <param name="timeProvider">The optional clock used by the request context.</param>
    public static RequestDelivery For(IRequestBase request, string? name, RequestInvocation invocation,
        RequestMetadata metadata, string? actorToken = null, RequestTraceContext? traceContext = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RequestDelivery(name ?? request.GetType().Name, invocation, metadata, actorToken, traceContext, timeProvider);
    }
}

/// <summary>
/// Shares actor re-validation and request-bus dispatch across every inbound transport while
/// keeping the validator and bus as explicit abstractions supplied by the adapter.
/// </summary>
public static class RequestDispatch
{
    /// <summary>Re-validates and dispatches a request with propagated W3C trace context.</summary>
    /// <param name="actorValidator">The transport-boundary actor-token validator.</param>
    /// <param name="bus">The request bus that executes validated requests.</param>
    /// <param name="request">The deserialized request.</param>
    /// <param name="delivery">The transport's facts about this delivery.</param>
    /// <param name="ct">Cancels actor validation and dispatch.</param>
    /// <returns>The validation or dispatch outcome and whether the bus was invoked.</returns>
    /// <remarks>Creates one consumer activity. Scheduled invocations start a new trace linked to
    /// the delivery's trace context; other inbound invocations use it as their parent.</remarks>
    public static async ValueTask<RequestDispatchOutcome> SendAsync(
        IRequestActorValidator actorValidator,
        IRequestBus bus,
        IRequest request,
        RequestDelivery delivery,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actorValidator);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(delivery);

        using var process = StartProcess(delivery);
        var actorResult = await actorValidator.ValidateAsync(delivery.ActorToken, ct).ConfigureAwait(false);
        if (!actorResult.IsSuccess)
            return new RequestDispatchOutcome(Result.Failure(actorResult.Error), WasDispatched: false);

        var outcome = await bus.DispatchAsync(request, Context(Actor(actorResult, actorValidator), delivery), ct).ConfigureAwait(false);
        return new RequestDispatchOutcome(outcome, WasDispatched: true);
    }

    /// <summary>Re-validates and dispatches a result-bearing request with propagated W3C trace context.</summary>
    /// <typeparam name="TOut">The successful result value type.</typeparam>
    /// <param name="actorValidator">The transport-boundary actor-token validator.</param>
    /// <param name="bus">The request bus that executes validated requests.</param>
    /// <param name="request">The deserialized result-bearing request.</param>
    /// <param name="delivery">The transport's facts about this delivery.</param>
    /// <param name="ct">Cancels actor validation and dispatch.</param>
    /// <returns>The validation or dispatch outcome and whether the bus was invoked.</returns>
    /// <remarks>Creates one consumer activity. Scheduled invocations start a new trace linked to
    /// the delivery's trace context; other inbound invocations use it as their parent.</remarks>
    public static async ValueTask<RequestDispatchOutcome<TOut>> SendAsync<TOut>(
        IRequestActorValidator actorValidator,
        IRequestBus bus,
        IRequest<TOut> request,
        RequestDelivery delivery,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actorValidator);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(delivery);

        using var process = StartProcess(delivery);
        var actorResult = await actorValidator.ValidateAsync(delivery.ActorToken, ct).ConfigureAwait(false);
        if (!actorResult.IsSuccess)
            return new RequestDispatchOutcome<TOut>(Result<TOut>.Failure(actorResult.Error), WasDispatched: false);

        var outcome = await bus.DispatchAsync(request, Context(Actor(actorResult, actorValidator), delivery), ct).ConfigureAwait(false);
        return new RequestDispatchOutcome<TOut>(outcome, WasDispatched: true);
    }

    static System.Diagnostics.Activity? StartProcess(RequestDelivery delivery) => PortiaTelemetry.StartProcess(
        delivery.Name, delivery.Invocation.TransportName, delivery.TraceContext, delivery.Invocation is ScheduleInvocation);

    static RequestDispatchContext Context(System.Security.Claims.ClaimsPrincipal actor, RequestDelivery delivery) =>
        new(actor, delivery.Invocation, delivery.Metadata, delivery.TimeProvider);

    // Result<T> makes no non-null promise about a successful value — T is unconstrained, so
    // Result<T>.Success(null) is representable. The validator's contract does promise a principal
    // here, so an implementation that breaks it gets a named failure instead of a null reference
    // surfacing later inside dispatch.
    static System.Security.Claims.ClaimsPrincipal Actor(Result<System.Security.Claims.ClaimsPrincipal> validated,
        IRequestActorValidator validator) => validated.Value ?? throw new InvalidOperationException(
            $"Portia actor validator '{validator.GetType().FullName}' returned a successful result with no principal.");
}

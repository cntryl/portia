using System.Diagnostics;
using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     The outcome of re-validating an actor token and conditionally dispatching a result-bearing request.
/// </summary>
/// <typeparam name="TOut">The request result type.</typeparam>
/// <param name="Outcome">The validation failure or request-bus outcome.</param>
/// <param name="WasDispatched">Whether actor validation succeeded and the bus was invoked.</param>
public readonly record struct RequestDispatchOutcome<TOut>(Result<TOut> Outcome, bool WasDispatched);

/// <summary>
///     Shares actor re-validation and request-bus dispatch across every inbound transport while
///     keeping the validator and bus as explicit abstractions supplied by the adapter.
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
    /// <remarks>
    ///     Creates one consumer activity. Scheduled invocations start a new trace linked to
    ///     the delivery's trace context; other inbound invocations use it as their parent.
    /// </remarks>
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
        {
            return new RequestDispatchOutcome(Result.Failure(actorResult.Error), false);
        }
        else
        {
            var outcome = await bus.DispatchAsync(request, Context(Actor(actorResult, actorValidator), delivery), ct)
                .ConfigureAwait(false);
            return new RequestDispatchOutcome(outcome, true);
        }
    }

    /// <summary>Re-validates and dispatches a result-bearing request with propagated W3C trace context.</summary>
    /// <typeparam name="TOut">The successful result value type.</typeparam>
    /// <param name="actorValidator">The transport-boundary actor-token validator.</param>
    /// <param name="bus">The request bus that executes validated requests.</param>
    /// <param name="request">The deserialized result-bearing request.</param>
    /// <param name="delivery">The transport's facts about this delivery.</param>
    /// <param name="ct">Cancels actor validation and dispatch.</param>
    /// <returns>The validation or dispatch outcome and whether the bus was invoked.</returns>
    /// <remarks>
    ///     Creates one consumer activity. Scheduled invocations start a new trace linked to
    ///     the delivery's trace context; other inbound invocations use it as their parent.
    /// </remarks>
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
        {
            return new RequestDispatchOutcome<TOut>(Result<TOut>.Failure(actorResult.Error), false);
        }
        else
        {
            var outcome = await bus.DispatchAsync(request, Context(Actor(actorResult, actorValidator), delivery), ct)
                .ConfigureAwait(false);
            return new RequestDispatchOutcome<TOut>(outcome, true);
        }
    }

    static Activity? StartProcess(RequestDelivery delivery) => PortiaTelemetry.StartProcess(
        delivery.Name, delivery.Invocation.TransportName, delivery.TraceContext,
        delivery.Invocation is ScheduleInvocation);

    static RequestDispatchContext Context(ClaimsPrincipal actor, RequestDelivery delivery) =>
        new(actor, delivery.Invocation, delivery.Metadata, delivery.TimeProvider);

    // Result<T> makes no non-null promise about a successful value — T is unconstrained, so
    // Result<T>.Success(null) is representable. The validator's contract does promise a principal
    // here, so an implementation that breaks it gets a named failure instead of a null reference
    // surfacing later inside dispatch.
    static ClaimsPrincipal Actor(Result<ClaimsPrincipal> validated,
        IRequestActorValidator validator) => validated.Value ?? throw new InvalidOperationException(
        $"Portia actor validator '{validator.GetType().FullName}' returned a successful result with no principal.");
}

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
    public static async ValueTask<RequestDispatchOutcome> SendAsync(
        IRequestActorValidator actorValidator,
        IRequestBus bus,
        IRequest request,
        string? actorToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actorValidator);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(request);

        var actorResult = await actorValidator.ValidateAsync(actorToken, ct).ConfigureAwait(false);

        return actorResult is not { IsSuccess: true, Value: { } actor }
            ? new RequestDispatchOutcome(Result.Failure(actorResult.Error!), WasDispatched: false)
            : new RequestDispatchOutcome(
                await bus.SendAsync(request, actor, ct).ConfigureAwait(false),
                WasDispatched: true);
    }

    /// <summary>
    /// Re-validates an actor token and dispatches a result-bearing request when validation succeeds.
    /// </summary>
    public static async ValueTask<RequestDispatchOutcome<TOut>> SendAsync<TOut>(
        IRequestActorValidator actorValidator,
        IRequestBus bus,
        IRequest<TOut> request,
        string? actorToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actorValidator);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(request);

        var actorResult = await actorValidator.ValidateAsync(actorToken, ct).ConfigureAwait(false);

        return actorResult is not { IsSuccess: true, Value: { } actor }
            ? new RequestDispatchOutcome<TOut>(Result<TOut>.Failure(actorResult.Error!), WasDispatched: false)
            : new RequestDispatchOutcome<TOut>(
                await bus.SendAsync(request, actor, ct).ConfigureAwait(false),
                WasDispatched: true);
    }
}

namespace Cntryl.Portia;

/// <summary>
///     The outcome of re-validating an actor token and conditionally dispatching a no-result request.
/// </summary>
/// <param name="Outcome">The validation failure or request-bus outcome.</param>
/// <param name="WasDispatched">Whether actor validation succeeded and the bus was invoked.</param>
public readonly record struct RequestDispatchOutcome(Result Outcome, bool WasDispatched);

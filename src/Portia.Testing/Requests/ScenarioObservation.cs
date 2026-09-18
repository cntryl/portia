namespace Cntryl.Portia.Testing;

sealed record ScenarioObservation(
    Type RequestType,
    Type HandlerType,
    IReadOnlyList<Type> Guards,
    IReadOnlyList<LifecycleEntry> Lifecycle,
    ScenarioOutcome Outcome)
{
    public bool Authorized =>
        Lifecycle.Any(entry => entry.Phase is LifecyclePhase.Authorizer or LifecyclePhase.Permission);

    public bool ProceededPastAuthorization =>
        Lifecycle.Any(entry =>
            entry.Phase is LifecyclePhase.Behavior or LifecyclePhase.Guard or LifecyclePhase.Handler);

    public bool Handled => Lifecycle.Any(entry => entry.Phase == LifecyclePhase.Handler);
}

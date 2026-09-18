namespace Cntryl.Portia.Testing;

static class ScenarioExpectations
{
    public static string? Authorized(ScenarioObservation observation) =>
        !observation.Authorized
            ? "expected authorization to run and allow the request, but no request authorizer or permission check ran"
            : !observation.ProceededPastAuthorization && !observation.Outcome.IsSuccess
                ? $"expected authorization to allow the request, but it was denied ({observation.Outcome.Error!.Kind})"
                : null;

    public static string? Denied(ScenarioObservation observation, RequestErrorKind? kind)
    {
        var denied = observation is { Authorized: true, ProceededPastAuthorization: false, Outcome.IsSuccess: false };
        if (!denied)
        {
            return observation.Outcome.IsSuccess
                ? "expected the request to be denied by authorization, but it succeeded"
                : !observation.Authorized
                    ? "expected the request to be denied by authorization, but no request authorizer or permission check ran"
                    : $"expected the request to be denied by authorization, but authorization allowed it and it failed later ({observation.Outcome.Error!.Kind})";
        }

        return kind is { } expected && observation.Outcome.Error!.Kind != expected
            ? $"expected the request to be denied with {expected}, but it was denied with {observation.Outcome.Error.Kind}"
            : null;
    }

    public static string? GuardPassed(ScenarioObservation observation, Type guard)
    {
        if (NotApplicable(observation, guard) is { } notApplicable)
            return notApplicable;
        var index = LastIndex(observation, guard);
        return index < 0
            ? $"expected guard {guard.Name} to pass, but it did not run"
            : observation.Lifecycle.Skip(index + 1)
                .Any(entry => entry.Phase is LifecyclePhase.Guard or LifecyclePhase.Handler)
                ? null
                : $"expected guard {guard.Name} to pass, but it failed";
    }

    public static string? GuardFailed(ScenarioObservation observation, Type guard, RequestErrorKind? kind)
    {
        if (NotApplicable(observation, guard) is { } notApplicable)
            return notApplicable;
        var index = LastIndex(observation, guard);
        if (index < 0)
            return $"expected guard {guard.Name} to fail, but it did not run";
        if (observation.Lifecycle.Skip(index + 1)
                .Any(entry => entry.Phase is LifecyclePhase.Guard or LifecyclePhase.Handler)
            || observation.Outcome.IsSuccess)
            return $"expected guard {guard.Name} to fail, but it passed";
        return kind is { } expected && observation.Outcome.Error!.Kind != expected
            ? $"expected guard {guard.Name} to fail with {expected}, but it failed with {observation.Outcome.Error.Kind}"
            : null;
    }

    public static string? Handled(ScenarioObservation observation) => observation.Handled
        ? null
        : $"expected handler {observation.HandlerType.Name} to run, but it did not";

    public static string? NotHandled(ScenarioObservation observation) => observation.Handled
        ? $"expected handler {observation.HandlerType.Name} not to run, but it did"
        : null;

    public static string? Success(ScenarioObservation observation) => observation.Outcome.IsSuccess
        ? null
        : $"expected success, but the result was {observation.Outcome}";

    public static string? Failure(ScenarioObservation observation, RequestErrorKind? kind) =>
        observation.Outcome.IsSuccess
            ? "expected failure, but the request succeeded"
            : kind is { } expected && observation.Outcome.Error!.Kind != expected
                ? $"expected failure with {expected}, but the result was {observation.Outcome}"
                : null;

    static string? NotApplicable(ScenarioObservation observation, Type guard) =>
        observation.Guards.Contains(guard)
            ? null
            : $"{guard.Name} is not a guard registered for {observation.RequestType.Name}; registered guards: "
              + (observation.Guards.Count == 0
                  ? "none"
                  : string.Join(", ", observation.Guards.Select(type => type.Name)));

    static int LastIndex(ScenarioObservation observation, Type component)
    {
        for (var index = observation.Lifecycle.Count - 1; index >= 0; index--)
        {
            if (observation.Lifecycle[index].Component == component)
                return index;
        }

        return -1;
    }
}

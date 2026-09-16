using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Testing;

sealed record ScenarioDefinition<TPayload>(
    IServiceProvider Services,
    ClaimsPrincipal Actor,
    RequestMetadata? Metadata,
    IRequestBase Request,
    Func<IRequestBus, RequestDispatchContext, Task<(ScenarioOutcome Outcome, TPayload Payload)>> Dispatch);

sealed record ScenarioOutcome(bool IsSuccess, RequestError? Error)
{
    public static ScenarioOutcome From(bool isSuccess, RequestError? error) => new(isSuccess, error);

    public override string ToString() => IsSuccess ? "success" : $"failure({Error!.Kind}) \"{Error.Message}\"";
}

enum LifecyclePhase
{
    Authorizer,
    Permission,
    Behavior,
    Guard,
    Handler
}

sealed record LifecycleEntry(Type Component, LifecyclePhase Phase)
{
    public override string ToString() => Phase switch
    {
        LifecyclePhase.Permission => "permission",
        _ => $"{Phase.ToString().ToLowerInvariant()} {Component.Name}"
    };
}

sealed record ScenarioObservation(
    Type RequestType,
    Type HandlerType,
    IReadOnlyList<Type> Guards,
    IReadOnlyList<LifecycleEntry> Lifecycle,
    ScenarioOutcome Outcome)
{
    public bool Authorized => Lifecycle.Any(entry => entry.Phase is LifecyclePhase.Authorizer or LifecyclePhase.Permission);

    public bool ProceededPastAuthorization =>
        Lifecycle.Any(entry => entry.Phase is LifecyclePhase.Behavior or LifecyclePhase.Guard or LifecyclePhase.Handler);

    public bool Handled => Lifecycle.Any(entry => entry.Phase == LifecyclePhase.Handler);
}

// Every lifecycle component is resolved from the bus's provider immediately before it runs, so recording
// top-level resolutions observes the real lifecycle order without changing how anything executes.
sealed class RecordingServiceProvider(IServiceProvider inner) : IServiceProvider, ISupportRequiredService
{
    readonly List<Type> _resolved = [];
    readonly Lock _gate = new();

    public IReadOnlyList<Type> Resolved
    {
        get
        {
            lock (_gate)
                return [.. _resolved];
        }
    }

    public object? GetService(Type serviceType)
    {
        var service = inner.GetService(serviceType);
        if (service is not null)
            Record(serviceType);
        return service;
    }

    public object GetRequiredService(Type serviceType)
    {
        var service = inner.GetRequiredService(serviceType);
        Record(serviceType);
        return service;
    }

    void Record(Type serviceType)
    {
        lock (_gate)
            _resolved.Add(serviceType);
    }
}

static class ScenarioEngine
{
    internal static async Task<TPayload> RunAsync<TPayload>(ScenarioDefinition<TPayload> definition,
        IReadOnlyList<Func<ScenarioObservation, TPayload, string?>> expectations)
    {
        var (observation, payload) = await ObserveAsync(definition).ConfigureAwait(false);
        var failures = expectations.Select(expectation => expectation(observation, payload))
            .OfType<string>()
            .ToArray();
        if (failures.Length == 0)
            return payload;
        throw new ScenarioExpectationException(
            $"Request scenario for '{observation.RequestType.Name}' did not meet {failures.Length} expectation(s):"
            + string.Concat(failures.Select(failure => $"{Environment.NewLine}  - {failure}"))
            + $"{Environment.NewLine}Observed lifecycle: {Trace(observation)}; result: {observation.Outcome}");
    }

    static async Task<(ScenarioObservation Observation, TPayload Payload)> ObserveAsync<TPayload>(
        ScenarioDefinition<TPayload> definition)
    {
        await using var scope = definition.Services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetService<RequestRegistry>()
                       ?? throw new InvalidOperationException(
                           "RequestScenario requires a provider composed with AddPortia(), or one that registers a RequestRegistry.");
        var requestType = definition.Request.GetType();
        var handlerType = registry.Handler(requestType).HandlerType;
        var policies = registry.Policies(requestType);
        var recorder = new RecordingServiceProvider(scope.ServiceProvider);
        var bus = new RequestBus(recorder, registry);
        var (outcome, payload) = await definition.Dispatch(bus, bus.CreateContext(definition.Actor, definition.Metadata))
            .ConfigureAwait(false);

        var authorizers = policies.PrincipalAuthorizers.Concat(policies.ResourceAuthorizers)
            .Select(registration => registration.AuthorizerType).ToHashSet();
        var guards = policies.Guards.Select(registration => registration.GuardType).ToArray();
        var behaviors = policies.Behaviors.Select(registration => registration.BehaviorType).ToHashSet();
        var lifecycle = recorder.Resolved.Select(type => Classify(type, handlerType, authorizers, guards, behaviors))
            .OfType<LifecycleEntry>()
            .ToArray();
        return (new ScenarioObservation(requestType, handlerType, guards, lifecycle, outcome), payload);
    }

    static LifecycleEntry? Classify(Type type, Type handler, HashSet<Type> authorizers, Type[] guards,
        HashSet<Type> behaviors) =>
        type == handler ? new LifecycleEntry(type, LifecyclePhase.Handler)
        : authorizers.Contains(type) ? new LifecycleEntry(type, LifecyclePhase.Authorizer)
        : Array.IndexOf(guards, type) >= 0 ? new LifecycleEntry(type, LifecyclePhase.Guard)
        : behaviors.Contains(type) ? new LifecycleEntry(type, LifecyclePhase.Behavior)
        : type == typeof(IPermissionEvaluator) ? new LifecycleEntry(type, LifecyclePhase.Permission)
        : null;

    static string Trace(ScenarioObservation observation) => observation.Lifecycle.Count == 0
        ? "(nothing ran)"
        : string.Join(" -> ", observation.Lifecycle);
}

// Expectation rules shared by every request shape. Each returns null when met, or what went wrong.
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
            : observation.Lifecycle.Skip(index + 1).Any(entry => entry.Phase is LifecyclePhase.Guard or LifecyclePhase.Handler)
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
        if (observation.Lifecycle.Skip(index + 1).Any(entry => entry.Phase is LifecyclePhase.Guard or LifecyclePhase.Handler)
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
              + (observation.Guards.Count == 0 ? "none" : string.Join(", ", observation.Guards.Select(type => type.Name)));

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

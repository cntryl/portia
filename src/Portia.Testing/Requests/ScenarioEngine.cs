using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Testing;

// Every lifecycle component is resolved from the bus's provider immediately before it runs, so recording
// top-level resolutions observes the real lifecycle order without changing how anything executes.

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
        var (outcome, payload) = await definition
            .Dispatch(bus, bus.CreateContext(definition.Actor, definition.Metadata))
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

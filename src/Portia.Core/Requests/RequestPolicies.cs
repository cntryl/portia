using System.Collections.Concurrent;

namespace Cntryl.Portia;

/// <summary>The name, authorizers, behaviors, and guards that apply to one concrete request type.</summary>
/// <param name="name">
///     The request's stable telemetry name: its declared
///     <see cref="DiscriminatorAttribute" /> name where it has one, so a CLR rename cannot silently
///     re-key spans and metrics, and the CLR type name only for a request that declares no
///     discriminator because it is never transported.
/// </param>
/// <param name="authorizers">
///     Applicable authorizers ascending by stage, including the request's declared permission at
///     <see cref="AuthorizationStage.Permission" />.
/// </param>
/// <param name="behaviorsInnermostFirst">
///     Applicable behaviors, descending by order, so wrapping
///     them in sequence leaves the lowest order outermost.
/// </param>
/// <param name="guards">Applicable guards in application registration order.</param>
/// <param name="isUnprotected">
///     Whether authorization is required but nothing protects this request, so dispatch must refuse it.
/// </param>
sealed class RequestPolicies(
    string name,
    RequestAuthorizerRegistration[] authorizers,
    RequestPipelineBehaviorRegistration[] behaviorsInnermostFirst,
    RequestGuardRegistration[] guards,
    bool isUnprotected = false)
{
    readonly ConcurrentDictionary<Type, object> _results = new();
    readonly ConcurrentDictionary<Type, object> _streams = new();

    readonly Lazy<UnaryRequestPipelinePlan> _unary = new(
        () => new UnaryRequestPipelinePlan(behaviorsInnermostFirst, guards),
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal string Name { get; } = name;
    internal RequestAuthorizerRegistration[] Authorizers { get; } = authorizers;
    internal bool HasAuthorizers => Authorizers.Length != 0;
    internal bool HasGuards => guards.Length != 0;
    internal RequestGuardRegistration[] Guards => guards;
    internal RequestPipelineBehaviorRegistration[] Behaviors { get; } = behaviorsInnermostFirst;

    internal bool IsUnprotected { get; } = isUnprotected;
    internal UnaryRequestPipelinePlan Unary => _unary.Value;

    // Dispatch asks for the plan on every call, so the lookup has to be free once the plan is
    // cached. A factory that captures this allocates a closure per call even on a hit; passing the
    // behaviors as the factory argument keeps the lambda static, and therefore cached.
    internal ResultRequestPipelinePlan<TOut> Result<TOut>() =>
        (ResultRequestPipelinePlan<TOut>)_results.GetOrAdd(typeof(TOut),
            static (_, state) => new ResultRequestPipelinePlan<TOut>(state.Behaviors, state.Guards),
            (Behaviors, Guards: guards));

    internal StreamRequestPipelinePlan<TOut> Stream<TOut>() =>
        (StreamRequestPipelinePlan<TOut>)_streams.GetOrAdd(typeof(TOut),
            static (_, state) => new StreamRequestPipelinePlan<TOut>(state.Behaviors, state.Guards),
            (Behaviors, Guards: guards));
}

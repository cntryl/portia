using System.Collections.Concurrent;

namespace Cntryl.Portia;

/// <summary>The name, authorizers, and behaviors that apply to one concrete request type.</summary>
/// <param name="name">
///     The request's stable telemetry name: its declared
///     <see cref="DiscriminatorAttribute" /> name where it has one, so a CLR rename cannot silently
///     re-key spans and metrics, and the CLR type name only for a request that declares no
///     discriminator because it is never transported.
/// </param>
/// <param name="principalAuthorizers">
///     Applicable authorizers that run before the declarative
///     permission check, ascending by stage.
/// </param>
/// <param name="resourceAuthorizers">
///     Applicable authorizers that run after it, ascending by
///     stage. The split is computed once here rather than re-filtered on every dispatch.
/// </param>
/// <param name="behaviorsInnermostFirst">
///     Applicable behaviors, descending by order, so wrapping
///     them in sequence leaves the lowest order outermost.
/// </param>
sealed class RequestPolicies(
    string name,
    RequestAuthorizerRegistration[] principalAuthorizers,
    RequestAuthorizerRegistration[] resourceAuthorizers,
    RequestPipelineBehaviorRegistration[] behaviorsInnermostFirst)
{
    readonly RequestPipelineBehaviorRegistration[] _behaviors = behaviorsInnermostFirst;
    readonly Lazy<UnaryRequestPipelinePlan> _unary = new(() => new(behaviorsInnermostFirst),
        LazyThreadSafetyMode.ExecutionAndPublication);
    readonly ConcurrentDictionary<Type, object> _results = new();
    readonly ConcurrentDictionary<Type, object> _streams = new();

    internal string Name { get; } = name;
    internal RequestAuthorizerRegistration[] PrincipalAuthorizers { get; } = principalAuthorizers;
    internal RequestAuthorizerRegistration[] ResourceAuthorizers { get; } = resourceAuthorizers;
    internal bool HasAuthorizers => PrincipalAuthorizers.Length != 0 || ResourceAuthorizers.Length != 0;
    internal UnaryRequestPipelinePlan Unary => _unary.Value;

    internal ResultRequestPipelinePlan<TOut> Result<TOut>() =>
        (ResultRequestPipelinePlan<TOut>)_results.GetOrAdd(typeof(TOut), _ =>
            new ResultRequestPipelinePlan<TOut>(_behaviors));

    internal StreamRequestPipelinePlan<TOut> Stream<TOut>() =>
        (StreamRequestPipelinePlan<TOut>)_streams.GetOrAdd(typeof(TOut), _ =>
            new StreamRequestPipelinePlan<TOut>(_behaviors));
}

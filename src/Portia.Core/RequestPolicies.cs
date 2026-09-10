namespace Cntryl.Portia;

/// <summary>The name, authorizers, and behaviors that apply to one concrete request type.</summary>
/// <param name="Name">
///     The request's stable telemetry name: its declared
///     <see cref="DiscriminatorAttribute" /> name where it has one, so a CLR rename cannot silently
///     re-key spans and metrics, and the CLR type name only for a request that declares no
///     discriminator because it is never transported.
/// </param>
/// <param name="PrincipalAuthorizers">
///     Applicable authorizers that run before the declarative
///     permission check, ascending by stage.
/// </param>
/// <param name="ResourceAuthorizers">
///     Applicable authorizers that run after it, ascending by
///     stage. The split is computed once here rather than re-filtered on every dispatch.
/// </param>
/// <param name="BehaviorsInnermostFirst">
///     Applicable behaviors, descending by order, so wrapping
///     them in sequence leaves the lowest order outermost.
/// </param>
sealed record RequestPolicies(
    string Name,
    RequestAuthorizerRegistration[] PrincipalAuthorizers,
    RequestAuthorizerRegistration[] ResourceAuthorizers,
    RequestPipelineBehaviorRegistration[] BehaviorsInnermostFirst);

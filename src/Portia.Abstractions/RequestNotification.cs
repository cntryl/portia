using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
/// A one-way request notification with either a carried token or a trusted scheduled system actor.
/// </summary>
/// <param name="Request">The delivered request.</param>
/// <param name="ActorToken">The delivering actor's raw bearer token, or <see langword="null" />
/// for an unauthenticated actor. Re-validate this (via <see cref="IRequestActorValidator" />)
/// rather than trusting it as-is.</param>
/// <param name="Metadata">The logical identity of this notification.</param>
/// <param name="Invocation">The concrete ingress facts.</param>
/// <param name="TraceContext">The optional propagated W3C trace fields. Scheduled notifications
/// use these fields as an activity link; other notifications use them as their parent.</param>
/// <param name="Actor">A trusted transport-resolved actor. Only scheduled system identities use
/// this path; token-carrying transports leave it null and require validation.</param>
public readonly record struct RequestNotification(IRequest Request, string? ActorToken, RequestMetadata Metadata, RequestInvocation Invocation,
    RequestTraceContext? TraceContext = null, ClaimsPrincipal? Actor = null);

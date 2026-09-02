namespace Cntryl.Portia;

/// <summary>
/// A request delivered live, alongside the raw actor token that traveled with it.
/// </summary>
/// <param name="Request">The delivered request.</param>
/// <param name="ActorToken">The delivering actor's raw bearer token, or <see langword="null" />
/// for an unauthenticated actor. Re-validate this (via <see cref="IRequestActorValidator" />)
/// rather than trusting it as-is.</param>
public readonly record struct LiveRequest(IRequest Request, string? ActorToken);

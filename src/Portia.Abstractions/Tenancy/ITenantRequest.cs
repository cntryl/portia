namespace Cntryl.Portia;

/// <summary>
///     A request that acts within one tenant. Implement it on a request, or on an application
///     request-family interface, and register <c>TenantRequestAuthorizer</c> once: every such request is
///     then denied unless the actor's own tenant, resolved by the application's
///     <see cref="IActorTenantResolver" />, is the tenant the request names. A system actor is exempt.
/// </summary>
public interface ITenantRequest : IRequestBase
{
    /// <summary>Gets the tenant the request acts within.</summary>
    TenantId Tenant { get; }
}

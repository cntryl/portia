namespace Cntryl.Portia;

/// <summary>
///     Denies an <see cref="ITenantRequest" /> whose tenant is not the actor's own. Select it once with
///     <c>AddRequestAuthorizer&lt;TenantRequestAuthorizer&gt;()</c> and it applies to every request that
///     implements <see cref="ITenantRequest" />, directly or through a request-family interface. The actor's
///     tenant comes from the application's <see cref="IActorTenantResolver" />, which hosted startup requires
///     once this authorizer is selected. A system actor is exempt, so reactions and schedules can act in any
///     tenant.
/// </summary>
/// <param name="tenants">Resolves the actor's tenant.</param>
public sealed class TenantRequestAuthorizer(IActorTenantResolver tenants) : IRequestAuthorizer<ITenantRequest>
{
    /// <inheritdoc />
    public async ValueTask<Result> AuthorizeAsync(IRequestContext<ITenantRequest> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (RequestActor.IsSystem(context.Actor))
            return Result.Success;

        var tenant = await tenants.ResolveAsync(context.Actor, ct).ConfigureAwait(false);
        return tenant == context.Request.Tenant
            ? Result.Success
            : RequestError.Forbidden("The actor does not belong to the request's tenant.");
    }
}

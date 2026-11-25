using Microsoft.Extensions.Configuration;

namespace Cntryl.Portia;

sealed class ProcessorStatusAuthorizer(IOperationalAuthorizer? policy = null, IConfiguration? configuration = null)
    : IRequestAuthorizer<IProcessorStatusRequest>
{
    public async ValueTask<Result> AuthorizeAsync(IRequestContext<IProcessorStatusRequest> context, CancellationToken ct)
    {
        var actor = context.Actor;

        if (!ActorAttribution.TryFromAuthenticatedPrincipal(actor, out _))
            return RequestError.Unauthorized("Processor status requires one unambiguous authenticated actor.");

        if (!SystemOperatorAuthorization.IsAdministrator(actor, configuration))
            return RequestError.Forbidden("Processor status requires the configured system administrator role.");

        if (context.Request.Tenant is { } tenant && !TenantId.TryParse(tenant, null, out _))
            return RequestError.Validation("Processor status requires a valid tenant identity.");


        var scope = new OperationalScope(context.Request.Tenant);
        return policy is null ||
               await policy.AuthorizeAsync(actor, scope, OperationalAccess.Read, ct).ConfigureAwait(false)
            ? Result.Success
            : RequestError.Forbidden("The actor may not inspect processors in this scope.");
    }
}

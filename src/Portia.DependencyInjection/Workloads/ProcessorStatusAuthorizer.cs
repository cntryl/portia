using Microsoft.Extensions.Configuration;

namespace Cntryl.Portia;

sealed class ProcessorStatusAuthorizer(IOperationalAuthorizer? policy = null, IConfiguration? configuration = null)
    : IRequestAuthorizer<GetProcessorStatus>
{
    public async ValueTask<Result> AuthorizeAsync(IRequestContext<GetProcessorStatus> context, CancellationToken ct)
    {
        var actor = context.Actor;
        if (!actor.Identities.Any(identity => identity.IsAuthenticated))
            return RequestError.Unauthorized("Processor status requires an authenticated actor.");
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

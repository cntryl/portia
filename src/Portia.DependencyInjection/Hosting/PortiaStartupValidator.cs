using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
///     Validates Portia's serialization, composed permission-evaluator, and required-authorization
///     boundaries before a hosted application starts accepting work.
/// </summary>
sealed class PortiaStartupValidator(
    [FromKeyedServices(PortiaServiceKeys.Json)]
    JsonSerializerOptions json,
    IDomainEventSerializer eventSerializer,
    RequestRegistry requests,
    PortiaStartupValidationRegistry validations,
    IServiceProviderIsService available,
    PortiaBuilder builder) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolution is the validation step: DI must construct the frozen JSON options and the
        // configured serializer before any endpoint or worker can deserialize an event. The
        // default JsonDomainEventSerializer constructor validates all upcaster identities and
        // transitions; retaining both references here makes that startup contract explicit.
        _ = json;
        _ = eventSerializer;
        validations.Validate();

        if (requests.PermissionRequestTypes.Count > 0 && !available.IsService(typeof(IPermissionEvaluator)))
        {
            var permissionProtected = requests.PermissionRequestTypes.Select(type => type.FullName ?? type.Name)
                .Order(StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"IPermissionEvaluator is required by permission-protected request types: {string.Join(", ", permissionProtected)}.");
        }


        if (available.IsService(typeof(TenantRequestAuthorizer)) && !available.IsService(typeof(IActorTenantResolver)))
        {
            throw new InvalidOperationException(
                "IActorTenantResolver is required by TenantRequestAuthorizer. Register the application's resolver, such as new ClaimTenantResolver(\"tenant_id\"), so the authorizer can compare each request's tenant with the actor's.");
        }

        if (builder.AuthorizationRequired && !requests.RequiresAuthorization)
        {
            throw new InvalidOperationException(
                "RequireAuthorization() is enabled, but the registered RequestRegistry was not composed by AddPortia(), so dispatch would not enforce it. Remove the custom RequestRegistry registration.");
        }

        if (requests.InvalidActorClassificationTypes.Count > 0)
        {
            var invalidActors = requests.InvalidActorClassificationTypes.Select(type => type.FullName ?? type.Name)
                .Order(StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Each listed request must implement exactly one of IAuthenticatedRequest, ISystemRequest, or IPublicRequest. Invalid actor classification: {string.Join(", ", invalidActors)}.");
        }

        if (requests.MissingActorAuthorizationTypes.Count > 0)
        {
            var missingActors = requests.MissingActorAuthorizationTypes.Select(type => type.FullName ?? type.Name)
                .Order(StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Actor-classified requests require a principal-stage authorizer bound to their actor family, or public requests must be allowed anonymous: {string.Join(", ", missingActors)}.");
        }

        if (requests.UnprotectedRequestTypes.Count > 0)
        {
            var unprotected = requests.UnprotectedRequestTypes.Select(type => type.FullName ?? type.Name)
                .Order(StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Authorization is required, but these request types have no applicable request authorizer or [RequiresPermission]: {string.Join(", ", unprotected)}. Register an authorizer, declare a permission, or allow them with RequireAuthorization(options => options.AllowAnonymous<TRequest>()).");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

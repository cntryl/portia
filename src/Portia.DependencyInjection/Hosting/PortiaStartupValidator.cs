using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
///     Validates Portia's serialization, composed permission-evaluator, and required-authorization
///     boundaries before a hosted application starts accepting work.
/// </summary>
sealed class PortiaStartupValidator(
    [FromKeyedServices(PortiaServiceKeys.Json)] JsonSerializerOptions json,
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

        if (builder.AuthorizationRequired && !requests.RequiresAuthorization)
        {
            throw new InvalidOperationException(
                "RequireAuthorization() is enabled, but the registered RequestRegistry was not composed by AddPortia(), so dispatch would not enforce it. Remove the custom RequestRegistry registration.");
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

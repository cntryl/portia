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
    IEnumerable<IPortiaStartupValidationParticipant> participants,
    IServiceProviderIsService available,
    PortiaBuilder builder) : IHostedLifecycleService
{
    bool _validated;
    bool _usesLifecycle;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _usesLifecycle = true;
        ValidateStartup();
        return Task.CompletedTask;
    }

    void ValidateStartup()
    {
        if (_validated)
            return;

        // Resolution is the validation step: DI must construct the frozen JSON options and the
        // configured serializer before any endpoint or worker can deserialize an event. The
        // default JsonDomainEventSerializer constructor validates all upcaster identities and
        // transitions; retaining both references here makes that startup contract explicit.
        _ = json;
        _ = eventSerializer;
        foreach (var participant in participants)
            participant.Initialize();
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

        _validated = true;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        // Legacy IWebHost implementations call only IHostedService.StartAsync, without the
        // IHostedLifecycleService pre-start callbacks. Retain the validation path for those hosts.
        ValidateStartup();
        if (!_usesLifecycle)
            CompleteStartup();
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        CompleteStartup();
        return Task.CompletedTask;
    }

    void CompleteStartup()
    {
        // Generic hosts finish building the HTTP pipeline in a hosted service's StartAsync.
        // Complete validation after every StartAsync, before Portia's later StartedAsync workers.
        // Legacy IWebHost builds the pipeline before calling hosted services at all.
        foreach (var participant in participants)
            participant.Complete();
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
///     Validates Portia's serialization, composed permission-evaluator, and required-authorization
///     boundaries before a hosted application starts accepting work.
/// </summary>
sealed class PortiaStartupValidator(
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

sealed class PortiaStartupValidationRegistry
{
    readonly Dictionary<string, Action> _validations = new(StringComparer.Ordinal);
    TaskCompletionSource _endpointValidations = CompletedSource();
    int _pendingEndpointValidations;
    bool _validated;

    public EndpointValidationRegistration BeginEndpointValidation()
    {
        lock (_validations)
        {
            if (_pendingEndpointValidations++ == 0)
                _endpointValidations = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return new(this);
    }

    public bool HasPendingEndpointValidations
    {
        get
        {
            lock (_validations)
                return _pendingEndpointValidations != 0;
        }
    }

    public Task WaitForEndpointValidationsAsync(CancellationToken ct)
    {
        Task ready;
        lock (_validations)
            ready = _endpointValidations.Task;
        return ready.WaitAsync(ct);
    }

    public void Add(string name, Action validation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(validation);
        lock (_validations)
        {
            if (_validated)
            {
                validation();
                return;
            }

            _validations.TryAdd(name, validation);
        }
    }

    public void Validate()
    {
        lock (_validations)
        {
            foreach (var validation in _validations.OrderBy(item => item.Key, StringComparer.Ordinal))
                validation.Value();
            _validated = true;
        }
    }

    void CompleteEndpointValidation()
    {
        TaskCompletionSource? ready = null;
        lock (_validations)
        {
            if (--_pendingEndpointValidations == 0)
                ready = _endpointValidations;
        }

        _ = ready?.TrySetResult();
    }

    static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    internal sealed class EndpointValidationRegistration(PortiaStartupValidationRegistry owner)
    {
        int _completed;

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                owner.CompleteEndpointValidation();
        }
    }
}

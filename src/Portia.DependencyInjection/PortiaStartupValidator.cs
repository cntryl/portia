using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
///     Validates Portia's serialization and composed permission-evaluator boundaries before a
///     hosted application starts accepting work.
/// </summary>
sealed class PortiaStartupValidator(
    JsonSerializerOptions json,
    IDomainEventSerializer eventSerializer,
    RequestRegistry requests,
    IServiceProviderIsService available) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolution is the validation step: DI must construct the frozen JSON options and the
        // configured serializer before any endpoint or worker can deserialize an event. The
        // default JsonDomainEventSerializer constructor validates all upcaster identities and
        // transitions; retaining both references here makes that startup contract explicit.
        _ = json;
        _ = eventSerializer;
        if (requests.PermissionRequestTypes.Count > 0 && !available.IsService(typeof(IPermissionEvaluator)))
        {
            var guarded = requests.PermissionRequestTypes.Select(type => type.FullName ?? type.Name)
                .Order(StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"IPermissionEvaluator is required by guarded request types: {string.Join(", ", guarded)}.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

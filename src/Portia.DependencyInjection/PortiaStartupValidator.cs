using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Validates Portia's serialization boundary before transport workers start.</summary>
sealed class PortiaStartupValidator(JsonSerializerOptions json, IDomainEventSerializer eventSerializer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolution is the validation step: DI must construct the frozen JSON options and the
        // configured serializer before any endpoint or worker can deserialize an event. The
        // default JsonDomainEventSerializer constructor validates all upcaster identities and
        // transitions; retaining both references here makes that startup contract explicit.
        _ = json;
        _ = eventSerializer;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

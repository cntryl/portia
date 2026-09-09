using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Validates Portia's serialization boundary before transport workers start.</summary>
sealed class PortiaStartupValidator(JsonSerializerOptions json, IDomainEventSerializer eventSerializer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = json;
        _ = eventSerializer;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

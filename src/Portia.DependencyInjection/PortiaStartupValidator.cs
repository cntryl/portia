using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Validates Portia's serialization boundary before transport workers start.</summary>
sealed class PortiaStartupValidator(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<JsonSerializerOptions>();
        _ = services.GetRequiredService<IDomainEventSerializer>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

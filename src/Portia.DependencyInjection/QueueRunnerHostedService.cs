using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
/// Runs a <see cref="QueueRunner" /> for the life of the host. Registered by
/// <see cref="PortiaHostingServiceCollectionExtensions.AddPortiaQueueRunner" /> — not meant to be
/// constructed directly.
/// </summary>
/// <param name="runner">The runner to host.</param>
sealed class QueueRunnerHostedService(QueueRunner runner) : BackgroundService
{
    readonly QueueRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _runner.RunAsync(stoppingToken);
}

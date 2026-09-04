using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
/// Runs a <see cref="RequestNotificationRunner" /> for the life of the host. Registered by
/// <see cref="PortiaHostingServiceCollectionExtensions.AddPortiaRequestNotificationRunner" /> —
/// not meant to be constructed directly.
/// </summary>
/// <param name="runner">The runner to host.</param>
sealed class RequestNotificationRunnerHostedService(RequestNotificationRunner runner) : BackgroundService
{
    readonly RequestNotificationRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _runner.RunAsync(stoppingToken);
}

using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
/// Runs a <see cref="LiveRequestRunner" /> for the life of the host. Registered by
/// <see cref="PortiaHostingServiceCollectionExtensions.AddPortiaLiveRequestRunner" /> — not meant
/// to be constructed directly.
/// </summary>
/// <param name="runner">The runner to host.</param>
sealed class LiveRequestRunnerHostedService(LiveRequestRunner runner) : BackgroundService
{
    readonly LiveRequestRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _runner.RunAsync(stoppingToken);
}

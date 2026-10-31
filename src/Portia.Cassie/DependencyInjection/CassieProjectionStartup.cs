using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Initializes and verifies checkpoint metadata while the host starts, before any workload runs.</summary>
sealed class CassieProjectionStartup(CassieProjectionDatabase database) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken) =>
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

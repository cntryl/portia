using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

sealed class ComponentHostedService<TComponent>(
    IServiceScopeFactory scopeFactory,
    Func<IServiceProvider, CancellationToken, ValueTask> runPass,
    TimeSpan pollInterval,
    TimeProvider timeProvider,
    ILogger<ComponentHostedService<TComponent>>? logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await runPass(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PortiaTelemetry.RecordRunnerFault(typeof(TComponent).Name, "component pass failed", ex, logger);
            }

            try
            {
                await Task.Delay(pollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

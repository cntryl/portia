using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

// Restarts transport enumeration only. Individual message redelivery belongs to the broker.
sealed class QueueRunnerHostedService(
    QueueRunner runner,
    TimeProvider? timeProvider = null,
    ILogger<QueueRunnerHostedService>? logger = null) : BackgroundService
{
    readonly QueueRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunAsync(stoppingToken).ConfigureAwait(false);
                if (!stoppingToken.IsCancellationRequested)
                    PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), "consumer completed without cancellation", logger: logger);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), "consumer enumeration faulted", ex, logger);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

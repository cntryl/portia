using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

// Restarts transport enumeration only. Individual message redelivery belongs to the broker.
sealed class QueueRunnerHostedService(
    QueueRunner runner,
    TimeProvider? timeProvider = null,
    ILogger<QueueRunnerHostedService>? logger = null) : BackgroundService
{
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly QueueRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunAsync(stoppingToken).ConfigureAwait(false);
                if (!stoppingToken.IsCancellationRequested)
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Execution, logger: logger);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (TerminalHandlerFailureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Execution, ex, logger);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

sealed class RecordingApplicationLifetime : IHostApplicationLifetime
{
    readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task StopRequested => _stopRequested.Task;
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => _stopRequested.SetResult();
}

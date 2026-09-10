using Cntryl.Fitz;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

sealed class FitzApplicationConnection(Client client, bool owned, TimeSpan timeout) : IHostedService, IAsyncDisposable
{
    readonly SemaphoreSlim _gate = new(1);
    bool _connected;
    bool _disposed;

    internal Client Client { get; } = client;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (owned)
            {
                await Client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connected)
            {
                return;
            }

            if (owned)
            {
                await Client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(timeout), cancellationToken)
                    .ConfigureAwait(false);
            }

            _connected = true;
        }
        catch
        {
            if (owned && !_disposed)
            {
                _disposed = true;
                await Client.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    // Disposal follows hosted-service shutdown, including concurrent StopAsync settings.
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

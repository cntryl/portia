using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

sealed class FitzProcessorStatusRpcRegistrar(IRpcClient rpc,
    IServiceScopeFactory scopes, RequestTransportCatalog catalog, Func<string> application,
    ILogger<FitzProcessorStatusRpcRegistrar>? logger = null,
    TimeProvider? timeProvider = null) : IProcessorStatusRpcRegistrar, IAsyncDisposable
{
    readonly Func<CancellationToken, Task> _connect = static _ => Task.CompletedTask;
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, RpcWorkerRegistration> _withdrawals = new(StringComparer.Ordinal);
    readonly CancellationTokenSource _cleanupLifetime = new();
    readonly SemaphoreSlim _cleanupWake = new(0, 1);
    readonly Lock _cleanupGate = new();
    Task? _cleanupTask;
    bool _disposed;

    internal FitzProcessorStatusRpcRegistrar(FitzApplicationConnection connection,
        IServiceScopeFactory scopes, RequestTransportCatalog catalog, Func<string> application,
        ILogger<FitzProcessorStatusRpcRegistrar>? logger = null,
        TimeProvider? timeProvider = null)
        : this(connection.Client.Rpc, scopes, catalog, application, logger, timeProvider) => _connect = connection.StartAsync;

    public async ValueTask<IAsyncDisposable> RegisterAsync(WorkloadIdentity workload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workload);
        lock (_cleanupGate)
            ObjectDisposedException.ThrowIf(_disposed, this);
        await _connect(ct).ConfigureAwait(false);
        var route = FitzProcessorStatusRoutes.Route(application(), workload);
        // A previous owner may have revoked local dispatch while native withdrawal failed.
        // Retry that exact handle before registering the same pattern on this connection.
        if (_withdrawals.TryGetValue(route, out var pending))
        {
            await pending.UnregisterAsync(ct).ConfigureAwait(false);
            _ = _withdrawals.TryRemove(new KeyValuePair<string, RpcWorkerRegistration>(route, pending));
        }
        var owned = new FitzProcessorStatusRpcRegistration(workload, route, ct,
            registration => WithdrawAsync(route, registration));
        try
        {
            owned.Attach(await new FitzRpcRequestServer(rpc, scopes, catalog)
                .RegisterAsync<GetProcessorStatus, ProcessorStatus>(route, owned.Accepts, ct).ConfigureAwait(false));
            return owned;
        }
        catch
        {
            await owned.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task cleanup;
        lock (_cleanupGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            cleanup = _cleanupTask ?? Task.CompletedTask;
        }
        await _cleanupLifetime.CancelAsync().ConfigureAwait(false);
        await cleanup.ConfigureAwait(false);
        using var finalWithdrawalBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
        foreach (var pending in _withdrawals.ToArray())
        {
            if (finalWithdrawalBudget.IsCancellationRequested)
                break;
            _ = await TryWithdrawAsync(pending.Key, pending.Value, finalWithdrawalBudget.Token).ConfigureAwait(false);
        }
        _cleanupWake.Dispose();
        _cleanupLifetime.Dispose();
    }

    async ValueTask WithdrawAsync(string route, RpcWorkerRegistration registration)
    {
        _withdrawals[route] = registration;
        if (await TryWithdrawAsync(route, registration, CancellationToken.None).ConfigureAwait(false))
            return;
        lock (_cleanupGate)
        {
            if (_disposed)
                return;
            _cleanupTask ??= CleanupPendingAsync(_cleanupLifetime.Token);
            if (_cleanupWake.CurrentCount == 0)
                _ = _cleanupWake.Release();
        }
    }

    async Task CleanupPendingAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _cleanupWake.WaitAsync(ct).ConfigureAwait(false);
                var delay = TimeSpan.FromSeconds(1);
                while (!_withdrawals.IsEmpty)
                {
                    await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
                    foreach (var pending in _withdrawals.ToArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        _ = await TryWithdrawAsync(pending.Key, pending.Value, ct).ConfigureAwait(false);
                    }
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    async ValueTask<bool> TryWithdrawAsync(string route, RpcWorkerRegistration registration, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
            using var cleanupDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await registration.UnregisterAsync(cleanupDeadline.Token).ConfigureAwait(false);
            _ = _withdrawals.TryRemove(new KeyValuePair<string, RpcWorkerRegistration>(route, registration));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            // Keep the retryable handle. Native DisposeAsync deliberately swallows failures,
            // which would otherwise leave this pattern registered with no recovery path.
            PortiaTelemetry.RecordRunnerFault(nameof(FitzProcessorStatusRpcRegistrar), RunnerFaultStage.Cleanup,
                exception, logger);
            return false;
        }
    }
}

sealed class FitzProcessorStatusRpcRegistration(WorkloadIdentity workload, string route,
    CancellationToken ownership, Func<RpcWorkerRegistration, ValueTask>? withdraw = null) : IAsyncDisposable
{
    RpcWorkerRegistration? _registration;
    int _active = 1;

    internal void Attach(RpcWorkerRegistration registration) => _registration = registration;

    internal bool Accepts(GetProcessorStatus request, string received) =>
        Volatile.Read(ref _active) != 0 && !ownership.IsCancellationRequested
        && string.Equals(received, route, StringComparison.Ordinal)
        && string.Equals(request.Processor, workload.Name, StringComparison.Ordinal)
        && string.Equals(request.Tenant, workload.Tenant?.Value, StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        // Native withdrawal is best effort. Revoke local dispatch before awaiting it so a
        // retained subscription cannot answer for an owner that has released the workload.
        _ = Interlocked.Exchange(ref _active, 0);
        var registration = Interlocked.Exchange(ref _registration, null);
        if (registration is not null)
        {
            if (withdraw is not null)
                await withdraw(registration).ConfigureAwait(false);
            else
                await registration.DisposeAsync().ConfigureAwait(false);
        }
    }
}

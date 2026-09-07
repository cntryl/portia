using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

sealed partial class PortiaWorkloadService(IServiceProvider services) : BackgroundService, IHostedLifecycleService
{
    readonly WorkloadRegistration[] _registrations = [.. services.GetServices<WorkloadRegistration>()];
    readonly TimeProvider _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
    readonly ILogger<PortiaWorkloadService>? _logger = services.GetService<ILogger<PortiaWorkloadService>>();

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_registrations.Length == 0)
            return Task.CompletedTask;
        var available = services.GetRequiredService<IServiceProviderIsService>();
        Require(typeof(IDomainEventReader));
        if (_registrations.Any(item => item.Scope == WorkloadScope.PerTenant))
            Require(typeof(ITenantDirectory));
        return Task.CompletedTask;
        void Require(Type type)
        {
            if (!available.IsService(type))
                throw new InvalidOperationException($"Portia workers require '{type.Name}'. Register its application or infrastructure implementation.");
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await StartingAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_registrations.Length == 0)
            return;
        var active = new ConcurrentDictionary<WorkloadIdentity, WorkloadRegistration>();
        foreach (var registration in _registrations.Where(item => item.Scope == WorkloadScope.Global))
            active[new WorkloadIdentity(registration.Name)] = registration;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var perTenant = _registrations.Where(item => item.Scope == WorkloadScope.PerTenant).ToArray();
        var tenants = perTenant.Length == 0 ? Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token)
            : new MultiTenantRunner(services.GetRequiredService<ITenantDirectory>(), services.GetService<ILogger<MultiTenantRunner>>(), timeProvider: _clock).RunAsync(
                async (tenant, ct) =>
                {
                    var identities = perTenant.Select(item => new WorkloadIdentity(item.Name, tenant)).ToArray();
                    for (var i = 0; i < identities.Length; i++)
                        active[identities[i]] = perTenant[i];
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false); }
                    finally
                    {
                        foreach (var identity in identities)
                            _ = active.TryRemove(identity, out _);
                    }
                }, (_, _) => Task.CompletedTask, lifetime.Token);
        var coordinator = CoordinateAsync();
        async Task CoordinateAsync()
        {
            await ResolveCoordinator().RunAsync(
                () => [.. active.Keys],
                (identity, fencingToken, ct) => active.TryGetValue(identity, out var registration)
                    ? RunAsync(registration, identity, fencingToken, ct) : Task.CompletedTask, lifetime.Token).ConfigureAwait(false);
        }
        try
        {
            _ = await Task.WhenAny(tenants, coordinator).ConfigureAwait(false);
        }
        finally { await lifetime.CancelAsync().ConfigureAwait(false); }
        try { await Task.WhenAll(tenants, coordinator).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        if (!stoppingToken.IsCancellationRequested)
            throw new InvalidOperationException("The workload coordinator or tenant directory stopped unexpectedly.");
    }

    /// <summary>
    /// Uses the application's coordinator when infrastructure supplies one, and otherwise owns
    /// every workload in this process. Resolving the fallback here rather than registering it in
    /// <c>AddWorker()</c> keeps it immune to setup order: infrastructure registered after
    /// <c>AddWorker()</c> is still the coordinator that runs.
    /// </summary>
    IWorkloadCoordinator ResolveCoordinator()
    {
        if (services.GetService<IWorkloadCoordinator>() is { } coordinator)
            return coordinator;
        if (_logger is not null)
            LogSingleProcessCoordinator(_logger);
        // Reconcile cadence is infrastructure timing, not application time, so it deliberately
        // does not follow a registered TimeProvider — a test that controls the workload poll
        // interval with a fake clock would otherwise also be driving ownership reconciliation.
        return new SingleProcessWorkloadCoordinator();
    }

    async Task RunAsync(WorkloadRegistration registration, WorkloadIdentity identity, ulong fencingToken, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var provider = scope.ServiceProvider;
                provider.GetRequiredService<WorkloadContext>().Initialize(identity, fencingToken, registration.ExplicitName);
                if (registration.IsProjector)
                {
                    var projector = provider.GetServices<ProjectorRegistration>().Single(item => item.ProjectorType == registration.ComponentType);
                    await projector.RunPass(provider, registration.Processing, ct).ConfigureAwait(false);
                }
                else
                {
                    var reactor = (BaseReactor)provider.GetRequiredService(registration.ComponentType);
                    reactor.BindWorkload(identity, registration.ExplicitName);
                    var checkpoints = reactor.Checkpoints;
                    var checkpoint = await checkpoints.LoadAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern), ct).ConfigureAwait(false);
                    _ = await provider.GetRequiredService<ReactorRunner>().RunAsync(reactor, checkpoint,
                        registration.Processing.MaxBatchSize, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { PortiaTelemetry.RecordRunnerFault(nameof(PortiaWorkloadService), $"workload '{identity}' failed", ex, _logger); }
            try { await Task.Delay(registration.PollInterval, _clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No IWorkloadCoordinator is registered; owning every workload in this process. That is correct for a single worker replica only \u2014 register a distributed coordinator before scaling workers out.")]
    static partial void LogSingleProcessCoordinator(ILogger logger);

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

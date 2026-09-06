using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Wires Portia's background runners into a Microsoft.Extensions.Hosting host, as
/// <see cref="Microsoft.Extensions.Hosting.IHostedService" />s that start when the host starts
/// and stop cleanly when it shuts down. Without these, a runner is just a class an app has to
/// remember to construct and drive itself — nothing in Portia calls
/// <c>RunAsync</c> automatically.
///
/// Every extension here requires the runner's own dependencies to already be registered by the
/// app (an <see cref="IRequestQueueConsumer" /> for the queue runner, an
/// <see cref="ITenantDirectory" /> for the multi-tenant runner, and so on) — these extensions
/// only add the runner itself and the hosted service that drives it, the same "compose from
/// already-registered abstractions" shape as every other part of Portia's DI story.
/// </summary>
public static class PortiaHostingServiceCollectionExtensions
{
    /// <summary>
    /// Hosts a <see cref="QueueRunner" /> for the life of the host. Requires
    /// <see cref="IRequestQueueConsumer" />, <see cref="IRequestBus" />, and
    /// <see cref="IRequestActorValidator" /> to already be registered.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services" />, for chaining.</returns>
    public static IServiceCollection AddPortiaQueueRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => new QueueRunner(sp.GetRequiredService<IRequestQueueConsumer>(),
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<ILogger<QueueRunner>>()));
        _ = services.AddHostedService<QueueRunnerHostedService>();
        return services;
    }

    /// <summary>
    /// Hosts a <see cref="RequestNotificationRunner" /> for the life of the host. Requires
    /// <see cref="IRequestNotificationConsumer" />, <see cref="IRequestBus" />, and
    /// <see cref="IRequestActorValidator" /> to already be registered.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services" />, for chaining.</returns>
    public static IServiceCollection AddPortiaRequestNotificationRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => new RequestNotificationRunner(sp.GetRequiredService<IRequestNotificationConsumer>(),
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<ILogger<RequestNotificationRunner>>()));
        _ = services.AddHostedService<RequestNotificationRunnerHostedService>();
        return services;
    }

    /// <summary>
    /// Hosts a <see cref="MultiTenantRunner" /> for the life of the host. Requires
    /// <see cref="ITenantDirectory" /> and <typeparamref name="TWorkload" /> to already be
    /// registered. A fresh dependency-injection scope and workload instance are used for every
    /// active tenant.
    /// </summary>
    /// <typeparam name="TWorkload">The tenant-scoped component to run.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services" />, for chaining.</returns>
    public static IServiceCollection AddPortiaMultiTenantRunner<TWorkload>(this IServiceCollection services)
        where TWorkload : class, ITenantWorkload
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<MultiTenantRunner>();
        _ = services.AddHostedService<MultiTenantRunnerHostedService<TWorkload>>();
        return services;
    }

    /// <summary>
    /// Hosts a generated concrete projector with a fresh scope and authoritative checkpoint per pass.
    /// </summary>
    /// <typeparam name="TProjector">The concrete projector registered explicitly with Portia.</typeparam>
    /// <param name="services">The services.</param>
    /// <param name="options">Batching and rebuild options.</param>
    /// <param name="pollInterval">Delay between passes; defaults to one second.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddPortiaProjectorRunner<TProjector>(
        this IServiceCollection services,
        ProjectionRunOptions? options = null,
        TimeSpan? pollInterval = null)
        where TProjector : class
    {
        ArgumentNullException.ThrowIfNull(services);
        var interval = ValidateInterval(pollInterval);
        (options ?? ProjectionRunOptions.Default).Validate();
        _ = services.AddSingleton(new PortiaHostedComponentRegistration(typeof(TProjector)));
        services.TryAddScoped<ProjectorRunner>();
        _ = services.AddHostedService(sp =>
        {
            var registration = sp.GetServices<ProjectorRegistration>()
                .SingleOrDefault(r => r.ProjectorType == typeof(TProjector))
                ?? throw new InvalidOperationException($"No generated projector registration for '{typeof(TProjector)}'. Register it with portia.AddProjector first.");
            return new ComponentHostedService<TProjector>(
                sp.GetRequiredService<IServiceScopeFactory>(),
                (scope, ct) => registration.RunPass(scope, options, ct),
                interval,
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetService<ILogger<ComponentHostedService<TProjector>>>());
        });
        return services;
    }

    /// <summary>
    /// Hosts a <see cref="BaseReactor" /> as a continuous, checkpointed polling loop for the life of
    /// the host. Requires <typeparamref name="TReactor" /> and an
    /// <see cref="IProjectionCheckpointStore" /> to already be registered.
    /// </summary>
    /// <typeparam name="TReactor">The concrete reactor type.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="pollInterval">How long to wait between passes once caught up. Defaults to one
    /// second.</param>
    /// <returns><paramref name="services" />, for chaining.</returns>
    public static IServiceCollection AddPortiaReactorRunner<TReactor>(
        this IServiceCollection services,
        TimeSpan? pollInterval = null)
        where TReactor : BaseReactor =>
        AddPortiaReactorRunner<TReactor>(services, 512, pollInterval);

    /// <summary>
    /// Hosts a <see cref="BaseReactor" /> with bounded durable checkpoint batches.
    /// </summary>
    /// <typeparam name="TReactor">The concrete reactor type.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="maxBatchSize">How many events to process between checkpoint saves.</param>
    /// <param name="pollInterval">How long to wait between passes once caught up.</param>
    /// <returns><paramref name="services" />, for chaining.</returns>
    public static IServiceCollection AddPortiaReactorRunner<TReactor>(
        this IServiceCollection services,
        int maxBatchSize,
        TimeSpan? pollInterval = null)
        where TReactor : BaseReactor
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);

        var interval = ValidateInterval(pollInterval);
        _ = services.AddSingleton(new PortiaHostedComponentRegistration(typeof(TReactor)));
        services.TryAddScoped<ReactorRunner>();
        _ = services.AddHostedService(sp => new ComponentHostedService<TReactor>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            async (scope, ct) =>
            {
                var reactor = scope.GetRequiredService<TReactor>();
                var checkpoints = reactor.Checkpoints;
                var checkpoint = await checkpoints.LoadAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern), ct).ConfigureAwait(false);
                _ = await scope.GetRequiredService<ReactorRunner>()
                    .RunAsync(reactor, checkpoint, maxBatchSize, ct).ConfigureAwait(false);
            },
            interval,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ILogger<ComponentHostedService<TReactor>>>()));
        return services;
    }

    static TimeSpan ValidateInterval(TimeSpan? pollInterval)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(pollInterval));
        return interval;
    }
}

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

        services.TryAddSingleton<QueueRunner>();
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

        services.TryAddSingleton<RequestNotificationRunner>();
        _ = services.AddHostedService<RequestNotificationRunnerHostedService>();
        return services;
    }

    /// <summary>
    /// Hosts a <see cref="MultiTenantRunner" /> for the life of the host. Requires
    /// <see cref="ITenantDirectory" /> to already be registered.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="onTenantStarted">Runs for a tenant once it becomes active, until it's removed
    /// or the host shuts down — see <see cref="MultiTenantRunner.RunAsync" />.</param>
    /// <param name="onTenantStopped">Runs once for a tenant after it's removed.</param>
    /// <returns><paramref name="services" />, for chaining.</returns>
    public static IServiceCollection AddPortiaMultiTenantRunner(
        this IServiceCollection services,
        Func<IServiceProvider, TenantId, CancellationToken, Task> onTenantStarted,
        Func<IServiceProvider, TenantId, CancellationToken, Task> onTenantStopped)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(onTenantStarted);
        ArgumentNullException.ThrowIfNull(onTenantStopped);

        services.TryAddSingleton<MultiTenantRunner>();
        _ = services.AddHostedService(sp => new MultiTenantRunnerHostedService(sp, onTenantStarted, onTenantStopped));
        return services;
    }

    /// <summary>
    /// Hosts a <see cref="Projector{TProjection}" /> as a continuous, checkpointed polling loop
    /// for the life of the host. Requires <typeparamref name="TProjection" />'s own
    /// <see cref="Projector{TProjection}" /> and an <see cref="IProjectionCheckpointStore" /> to
    /// already be registered.
    /// </summary>
    /// <typeparam name="TProjection">The projection-specific application port.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="options">The batching and rebuild options for every pass.</param>
    /// <param name="pollInterval">How long to wait between passes once caught up. Defaults to one
    /// second.</param>
    /// <returns><paramref name="services" />, for chaining.</returns>
    public static IServiceCollection AddPortiaProjectorRunner<TProjection>(
        this IServiceCollection services,
        ProjectionRunOptions? options = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ProjectorRunner>();
        _ = services.AddHostedService(sp => new ProjectorHostedService<TProjection>(
            sp.GetRequiredService<ProjectorRunner>(),
            sp.GetRequiredService<Projector<TProjection>>(),
            sp.GetRequiredService<IProjectionCheckpointStore>(),
            options,
            pollInterval,
            sp.GetService<ILogger<ProjectorHostedService<TProjection>>>()));
        return services;
    }

    /// <summary>
    /// Hosts a <see cref="Reactor" /> as a continuous, checkpointed polling loop for the life of
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
        where TReactor : Reactor =>
        AddPortiaReactorRunner<TReactor>(services, 512, pollInterval);

    /// <summary>
    /// Hosts a <see cref="Reactor" /> with bounded durable checkpoint batches.
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
        where TReactor : Reactor
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);

        services.TryAddSingleton<ReactorRunner>();
        _ = services.AddHostedService(sp => new ReactorHostedService(
            sp.GetRequiredService<ReactorRunner>(),
            sp.GetRequiredService<TReactor>(),
            sp.GetRequiredService<IProjectionCheckpointStore>(),
            maxBatchSize,
            pollInterval,
            sp.GetService<ILogger<ReactorHostedService>>()));
        return services;
    }
}

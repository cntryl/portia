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

}

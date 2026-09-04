using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Registers explicit aggregate construction and scoped persistence.</summary>
public static class PortiaAggregateServiceCollectionExtensions
{
    /// <summary>Registers an aggregate factory and the event-store-backed repository.</summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <param name="services">The application services.</param>
    /// <param name="factory">Creates a new aggregate in the current scope.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddPortiaAggregate<TAggregate>(
        this IServiceCollection services,
        AggregateFactory<TAggregate> factory)
        where TAggregate : Aggregate
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        services.TryAddSingleton(factory);
        services.TryAddScoped<IAggregateRepository, AggregateRepository>();
        return services;
    }
}

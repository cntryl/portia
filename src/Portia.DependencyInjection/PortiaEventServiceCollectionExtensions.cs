using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Registers explicitly selected domain event types.</summary>
public static class PortiaEventServiceCollectionExtensions
{
    /// <summary>Contributes an event to the shared catalog. </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="services">Application services.</param>
    public static void AddPortiaEvent<TEvent>(this IServiceCollection services)
        where TEvent : DomainEvent
    {
        if (!services.Any(service => service.ServiceType == typeof(EventMarker<TEvent>)))
        {
            _ = services.AddSingleton(new EventMarker<TEvent>());
            _ = services.AddSingleton(new EventRegistration(static catalog => _ = catalog.Register<TEvent>()));
        }
    }

    internal static DomainEventTypeCatalog BuildCatalog(IServiceProvider services)
    {
        var catalog = new DomainEventTypeCatalog();
        foreach (var registration in services.GetServices<EventRegistration>())
            registration.Add(catalog);
        return catalog;
    }

    sealed class EventMarker<TEvent>;

    sealed record EventRegistration(Action<DomainEventTypeCatalog> Add);
}

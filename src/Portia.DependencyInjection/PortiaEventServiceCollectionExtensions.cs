using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Registers explicitly selected domain event types.</summary>
public static class PortiaEventServiceCollectionExtensions
{
    /// <summary>Contributes an event to the shared catalog. </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="services">Application services.</param>
    /// <param name="version">The payload schema version.</param>
    /// <param name="name">The stable logical name.</param>
    public static void AddPortiaEvent<TEvent>(this IServiceCollection services, int version, string name)
        where TEvent : DomainEvent
    {
        if (!services.Any(service => service.ServiceType == typeof(EventMarker<TEvent>)))
        {
            _ = services.AddSingleton(new EventMarker<TEvent>());
            _ = services.AddSingleton(new EventRegistration(catalog => _ = catalog.Register<TEvent>(version, name)));
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

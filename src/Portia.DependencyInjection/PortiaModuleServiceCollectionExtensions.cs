using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Marks an explicitly named partial module for compile-time registration generation.</summary>
/// <param name="imports">Other explicitly named modules included by this module.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PortiaModuleAttribute(params Type[] imports) : Attribute
{
    /// <summary>Gets the explicitly imported modules.</summary>
    public IReadOnlyList<Type> Imports { get; } = imports;
}

/// <summary>The generated registration contract for an explicitly named module.</summary>
public interface IPortiaModule
{
    /// <summary>Contributes this module's discovered components.</summary>
    /// <param name="services">Application services.</param>
    static abstract void Register(IServiceCollection services);
}

/// <summary>Composes compile-time-discovered modules without assembly scanning.</summary>
public static class PortiaModuleServiceCollectionExtensions
{
    /// <summary>Adds a generated module once, including its explicit imports.</summary>
    /// <typeparam name="TModule">The named partial module.</typeparam>
    /// <param name="services">Application services.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddPortiaModule<TModule>(this IServiceCollection services)
        where TModule : IPortiaModule
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(service => service.ServiceType == typeof(ModuleMarker<TModule>)))
            return services;
        var count = services.Count;
        try
        {
            _ = services.AddSingleton(new ModuleMarker<TModule>());
            services.TryAddScoped<IRequestBus, RequestBus>();
            services.TryAddSingleton<IReactorPrincipalProvider, SystemReactorPrincipalProvider>();
            services.TryAddSingleton<RequestRegistry>();
            services.TryAddSingleton(BuildCatalog);
            TModule.Register(services);
            _ = new RequestRegistry(
                services.Select(service => service.ImplementationInstance).OfType<RequestHandlerRegistration>(),
                services.Select(service => service.ImplementationInstance).OfType<RequestAuthorizerRegistration>());
            return services;
        }
        catch
        {
            while (services.Count > count)
                services.RemoveAt(services.Count - 1);
            throw;
        }
    }

    /// <summary>Contributes an event to the shared catalog. Intended for generated modules.</summary>
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

    static DomainEventTypeCatalog BuildCatalog(IServiceProvider services)
    {
        var catalog = new DomainEventTypeCatalog();
        foreach (var registration in services.GetServices<EventRegistration>())
            registration.Add(catalog);
        return catalog;
    }

    sealed class ModuleMarker<TModule>;

    sealed class EventMarker<TEvent>;

    sealed record EventRegistration(Action<DomainEventTypeCatalog> Add);
}

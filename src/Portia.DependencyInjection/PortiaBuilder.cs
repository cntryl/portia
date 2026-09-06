using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Composes shared application services and explicitly activated worker services.</summary>
public sealed class PortiaBuilder
{
    readonly Dictionary<string, Action<IServiceCollection>> _workers = new(StringComparer.Ordinal);
    bool _worker;

    internal PortiaBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>Gets the application's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Includes a generated module.</summary>
    public PortiaBuilder AddModule<TModule>() where TModule : IPortiaModule
    {
        _ = Services.AddPortiaModule<TModule>();
        return this;
    }

    /// <summary>Declares named worker-only registrations in shared application setup.</summary>
    public PortiaBuilder ConfigureWorker(string name, Action<IServiceCollection> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        if (_workers.TryGetValue(name, out var existing))
        {
            return existing != configure ? throw new InvalidOperationException($"Worker '{name}' has conflicting registrations.") : this;
        }
        if (_worker)
            configure(Services);
        _workers.Add(name, configure);
        return this;
    }

    /// <summary>Activates the shared application's worker registrations once in this host.</summary>
    public PortiaBuilder AddWorker()
    {
        if (_worker)
            return this;
        var count = Services.Count;
        try
        {
            foreach (var configure in _workers.Values)
                configure(Services);
            _worker = true;
        }
        catch
        {
            while (Services.Count > count)
                Services.RemoveAt(Services.Count - 1);
            throw;
        }
        return this;
    }
}

/// <summary>Registers shared Portia application setup in the standard DI container.</summary>
public static class PortiaApplicationServiceCollectionExtensions
{
    /// <summary>Configures the application's modules and capabilities without activating workers.</summary>
    public static PortiaBuilder AddPortia(this IServiceCollection services, Action<PortiaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = services.Select(service => service.ImplementationInstance).OfType<PortiaBuilder>().SingleOrDefault();
        if (builder is null)
        {
            builder = new PortiaBuilder(services);
            _ = services.AddSingleton(builder);
        }
        services.TryAddScoped<IAggregateRepository>(provider => new AggregateRepository(provider.GetRequiredService<IEventStore>()));
        configure(builder);
        return builder;
    }
}

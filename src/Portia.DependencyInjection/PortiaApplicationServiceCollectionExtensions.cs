using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Registers shared Portia application setup in the standard DI container.</summary>
public static class PortiaApplicationServiceCollectionExtensions
{
    static readonly ConditionalWeakTable<IServiceCollection, PortiaBuilder> Builders = [];

    // A private gate, not the caller's IServiceCollection: locking a public object another
    // library may also lock is how unrelated registration code ends up contending.
    static readonly Lock Gate = new();

    /// <summary>Creates or resumes the application's fluent Portia composition root.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>
    ///     The builder for this service collection — the same instance on every call, so shared
    ///     setup can be composed from more than one place.
    /// </returns>
    public static PortiaBuilder AddPortia(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PortiaBuilder builder;
        lock (Gate)
        {
            if (Builders.TryGetValue(services, out var existing))
            {
                builder = existing;
            }
            else
            {
                builder = new PortiaBuilder(services);
                Builders.Add(services, builder);
                _ = services.AddSingleton(builder);
            }
        }

        services.TryAddScoped<IAggregateRepository>(provider =>
            new AggregateRepository(provider.GetRequiredService<IEventStore>()));
        services.TryAddScoped<WorkloadContext>();
        services.TryAddScoped<IRequestBus, RequestBus>();
        services.TryAddSingleton<RequestRegistry>();
        services.TryAddSingleton(provider => provider.GetRequiredService<PortiaBuilder>().BuildJsonOptions());
        services.TryAddSingleton(PortiaEventServiceCollectionExtensions.BuildCatalog);
        services.TryAddSingleton<IDomainEventSerializer>(provider => new JsonDomainEventSerializer(
            provider.GetRequiredService<DomainEventTypeCatalog>(), provider.GetServices<IJsonDomainEventUpcaster>(),
            provider.GetRequiredService<JsonSerializerOptions>()));
        services.TryAddSingleton<IReactorPrincipalProvider, SystemReactorPrincipalProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PortiaStartupValidator>());
        return builder;
    }
}

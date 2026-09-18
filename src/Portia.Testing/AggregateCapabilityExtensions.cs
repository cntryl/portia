using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Testing;

/// <summary>Extensions for resolving <see cref="AggregateCapabilities" /> from a service provider.</summary>
public static class AggregateCapabilityExtensions
{
    /// <summary>
    ///     Combines the scope's <see cref="IAggregateReader" />, <see cref="IAggregateWriter" />, and
    ///     <see cref="IAggregateExecutor" />.
    /// </summary>
    /// <param name="services">The service provider or scope to resolve from.</param>
    /// <returns>The combined double.</returns>
    public static AggregateCapabilities Aggregates(this IServiceProvider services) =>
        new(services.GetRequiredService<IAggregateReader>(), services.GetRequiredService<IAggregateWriter>(),
            services.GetRequiredService<IAggregateExecutor>());
}

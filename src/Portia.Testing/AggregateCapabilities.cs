using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Testing;

/// <summary>The scope's aggregate read and write capabilities, for tests that exercise persistence directly.</summary>
public sealed class AggregateCapabilities(IAggregateReader reader, IAggregateWriter writer)
    : IAggregateReader, IAggregateWriter
{
    /// <inheritdoc />
    public ValueTask<TAggregate> HydrateAsync<TAggregate>(TAggregate aggregate, CancellationToken ct = default)
        where TAggregate : Aggregate => reader.HydrateAsync(aggregate, ct);

    /// <inheritdoc />
    public ValueTask SaveAsync<TAggregate>(TAggregate aggregate, IExecutionContext context,
        CancellationToken ct = default)
        where TAggregate : Aggregate => writer.SaveAsync(aggregate, context, ct);
}

/// <summary>Extensions for resolving <see cref="AggregateCapabilities" /> from a service provider.</summary>
public static class AggregateCapabilityExtensions
{
    /// <summary>Combines the scope's <see cref="IAggregateReader" /> and <see cref="IAggregateWriter" />.</summary>
    /// <param name="services">The service provider or scope to resolve from.</param>
    /// <returns>The combined double.</returns>
    public static AggregateCapabilities Aggregates(this IServiceProvider services) =>
        new(services.GetRequiredService<IAggregateReader>(), services.GetRequiredService<IAggregateWriter>());
}

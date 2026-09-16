using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

/// <summary>The scope's aggregate read and write capabilities, for tests that exercise persistence directly.</summary>
sealed class AggregateCapabilities(IAggregateReader reader, IAggregateWriter writer) : IAggregateReader, IAggregateWriter
{
    public ValueTask<TAggregate> HydrateAsync<TAggregate>(TAggregate aggregate, CancellationToken ct = default)
        where TAggregate : Aggregate => reader.HydrateAsync(aggregate, ct);

    public ValueTask SaveAsync<TAggregate>(TAggregate aggregate, IExecutionContext context,
        CancellationToken ct = default)
        where TAggregate : Aggregate => writer.SaveAsync(aggregate, context, ct);
}

static class AggregateCapabilityExtensions
{
    public static AggregateCapabilities Aggregates(this IServiceProvider services) =>
        new(services.GetRequiredService<IAggregateReader>(), services.GetRequiredService<IAggregateWriter>());
}

using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>
/// Base type for a user-defined event containing business data.
/// </summary>
public abstract record DomainEvent
{
    DomainEventMetadata? _metadata;

    /// <summary>
    /// Gets the event-sourcing metadata attached by Portia. Excluded from JSON serialization —
    /// a serializer carries metadata in its own envelope, separately from the event's business
    /// payload, so upcasters only ever see business fields.
    /// </summary>
    [JsonIgnore]
    public DomainEventMetadata Metadata => _metadata
        ?? throw new InvalidOperationException("The domain event has not been attached to an aggregate.");

    /// <summary>Attaches durable metadata once, for serializer adapters. Aggregate emissions assign their own metadata.</summary>
    /// <param name="metadata">The original event metadata.</param>
    /// <exception cref="InvalidOperationException">Metadata has already been attached.</exception>
    public void AttachMetadata(DomainEventMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (Interlocked.CompareExchange(ref _metadata, metadata, null) is not null)
            throw new InvalidOperationException("Domain event metadata can only be attached once.");
    }
}

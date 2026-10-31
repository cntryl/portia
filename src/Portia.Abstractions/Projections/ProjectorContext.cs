namespace Cntryl.Portia;

sealed class ProjectorContext(CheckpointIdentity identity) : IProjectorContext
{
    public CheckpointIdentity Identity { get; } = identity;
    public bool IsRebuild => Identity.RebuildId is not null;
}

/// <summary>The typed view of a source event and its projection batch, supplied by generated dispatch.</summary>
/// <typeparam name="TEvent">The event type handled.</typeparam>
public sealed class ProjectorContext<TEvent> : IProjectorContext<TEvent>
    where TEvent : DomainEvent
{
    /// <summary>Associates an event with its actual source record and shared batch metadata.</summary>
    /// <param name="ev">The event, which must be the same instance carried by <paramref name="source" />.</param>
    /// <param name="source">The stored record from which the event was read.</param>
    /// <param name="batch">The metadata shared by the atomic projection batch.</param>
    /// <exception cref="ArgumentException">The event is not the source record's own event.</exception>
    public ProjectorContext(TEvent ev, DomainEventRecord source, IProjectorContext batch)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(batch);
        if (!ReferenceEquals(ev, source.Event))
        {
            throw new ArgumentException("Projection context must refer to its actual source event.", nameof(source));
        }

        Trigger = ev;
        Source = source;
        Batch = batch;
    }

    /// <inheritdoc />
    public TEvent Trigger { get; }

    /// <inheritdoc />
    public DomainEventRecord Source { get; }

    /// <inheritdoc />
    public IProjectorContext Batch { get; }

    /// <inheritdoc />
    public CheckpointIdentity Identity => Batch.Identity;

    /// <inheritdoc />
    public bool IsRebuild => Batch.IsRebuild;
}

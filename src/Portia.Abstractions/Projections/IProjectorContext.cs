namespace Cntryl.Portia;

/// <summary>Shared projection-batch metadata. Application dependencies are injected through constructors.</summary>
public interface IProjectorContext
{
    /// <summary>Gets the component, stream pattern, and optional rebuild generation.</summary>
    CheckpointIdentity Identity { get; }

    /// <summary>Gets whether this execution is building a separate projection generation.</summary>
    bool IsRebuild { get; }
}

/// <summary>Carries one source event and the shared metadata of its projection batch.</summary>
/// <typeparam name="TEvent">The event type handled.</typeparam>
public interface IProjectorContext<out TEvent> : IProjectorContext
    where TEvent : DomainEvent
{
    /// <summary>Gets the typed event carried by <see cref="Source" />.</summary>
    TEvent Trigger { get; }

    /// <summary>Gets the actual source record, including its stream and offsets.</summary>
    DomainEventRecord Source { get; }

    /// <summary>Gets the metadata shared by every event in the atomic projection batch.</summary>
    IProjectorContext Batch { get; }
}

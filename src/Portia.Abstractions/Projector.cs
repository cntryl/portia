namespace Cntryl.Portia;

/// <summary>
/// Base type for a datastore-agnostic, batch-native projector.
/// </summary>
/// <typeparam name="TProjection">The projection-specific application port.</typeparam>
/// <param name="name">The stable projector name.</param>
/// <param name="pattern">The event stream pattern consumed by the projector. Wildcards are
/// expected to span more than the projector actually handles — the projector filters by event
/// type (its handler interfaces), not by narrowing the route pattern; an event type it does not
/// handle is silently skipped, not an error.</param>
/// <param name="target">The target that creates batch-scoped projection sessions.</param>
public abstract class Projector<TProjection>(
    string name,
    EventStreamPattern pattern,
    IProjectionTarget<TProjection> target)
{
    /// <summary>
    /// Gets the stable projector name used for checkpoints and rebuild generations.
    /// </summary>
    public string Name { get; } = !string.IsNullOrWhiteSpace(name)
        ? name
        : throw new ArgumentException("A projector name cannot be empty.", nameof(name));

    /// <summary>
    /// Gets the event stream pattern consumed by the projector.
    /// </summary>
    public EventStreamPattern Pattern { get; } = pattern ?? throw new ArgumentNullException(nameof(pattern));

    internal IProjectionTarget<TProjection> Target { get; } = target
        ?? throw new ArgumentNullException(nameof(target));

    internal ValueTask ProjectAsync(
        DomainEventRecord record,
        TProjection projection,
        bool isRebuild,
        CancellationToken ct) => ProjectEventAsync(record, new ProjectorContext<TProjection>(projection, isRebuild), ct);

    /// <summary>
    /// Dispatches one event through the batch-scoped projection application port.
    /// </summary>
    /// <param name="record">The event and its source offsets.</param>
    /// <param name="context">The projector context.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing asynchronous event handling.</returns>
    protected virtual ValueTask ProjectEventAsync(
        DomainEventRecord record,
        IProjectorContext<TProjection> context,
        CancellationToken ct) => throw new InvalidOperationException(
            $"Projector does not handle event type '{record.Ev.GetType()}'.");
}

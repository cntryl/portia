namespace Cntryl.Portia;

/// <summary>
/// Base type for a component that reacts to committed domain events by raising commands
/// against other aggregates through the aggregate repository.
/// </summary>
/// <param name="name">The stable reactor name.</param>
/// <param name="pattern">The event stream pattern consumed by the reactor. Wildcards are
/// expected to span more than the reactor actually handles — the reactor filters by event type
/// (its handler interfaces), not by narrowing the route pattern; an event type it does not
/// handle is silently skipped, not an error.</param>
public abstract class Reactor(string name, EventStreamPattern pattern)
{
    /// <summary>
    /// Gets the stable reactor name used for checkpoints.
    /// </summary>
    public string Name { get; } = !string.IsNullOrWhiteSpace(name)
        ? name
        : throw new ArgumentException("A reactor name cannot be empty.", nameof(name));

    /// <summary>
    /// Gets the event stream pattern consumed by the reactor.
    /// </summary>
    public EventStreamPattern Pattern { get; } = pattern ?? throw new ArgumentNullException(nameof(pattern));

    internal ValueTask ReactAsync(DomainEventRecord record, CancellationToken ct)
        => ReactToEventAsync(record, new ReactionExecutionContext(record, RequestActor.System), ct);

    internal ValueTask ReactAsync(DomainEventRecord record, IExecutionContext context, CancellationToken ct)
        => ReactToEventAsync(record, context, ct);

    /// <summary>Dispatches a triggering event with its system execution context.</summary>
    protected virtual ValueTask ReactToEventAsync(DomainEventRecord record, IExecutionContext context, CancellationToken ct)
        => throw new InvalidOperationException($"Reactor does not handle event type '{record.Ev.GetType()}'.");
}

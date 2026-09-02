namespace Cntryl.Portia;

/// <summary>
/// Base type for an event-sourced aggregate.
/// </summary>
/// <param name="id">The stable identity of the aggregate.</param>
/// <param name="stream">The stream that stores the aggregate's event history.</param>
public abstract class Aggregate(Uuid id, EventStreamAddress stream)
{
    readonly Dictionary<Type, Action<DomainEvent>> _handlers = [];
    readonly List<DomainEvent> _committedEvents = [];
    readonly HashSet<Uuid> _committedEventIds = [];
    readonly List<DomainEvent> _uncommittedEvents = [];
    readonly List<DomainEvent> _uncommittedAudits = [];

    /// <summary>
    /// Gets the stable identity of the aggregate.
    /// </summary>
    public Uuid Id { get; } = id != Uuid.Empty
        ? id
        : throw new ArgumentException("An aggregate ID cannot be empty.", nameof(id));

    /// <summary>
    /// Gets the stream that stores the aggregate's event history.
    /// </summary>
    public EventStreamAddress Stream { get; } = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>
    /// Gets the number of state-changing events applied to the aggregate.
    /// </summary>
    public ulong Version { get; private set; }

    internal IReadOnlyList<DomainEvent> CommittedEvents => _committedEvents;

    internal IReadOnlyList<DomainEvent> UncommittedEvents => _uncommittedEvents;

    internal IReadOnlyList<DomainEvent> UncommittedAudits => _uncommittedAudits;

    internal void Load(DomainEvent[] committedEvents)
    {
        ArgumentNullException.ThrowIfNull(committedEvents);

        if (_uncommittedEvents.Count != 0 || _uncommittedAudits.Count != 0)
            throw new InvalidOperationException("An aggregate with uncommitted changes cannot load committed events.");

        var eventIds = new HashSet<Uuid>();
        for (var index = 0; index < committedEvents.Length; index++)
        {
            var expectedVersion = checked(Version + (ulong)index + 1);
            ValidateCommittedEvent(committedEvents[index], expectedVersion, eventIds);
        }

        foreach (var ev in committedEvents)
        {
            Apply(ev);
            _committedEvents.Add(ev);
            _ = _committedEventIds.Add(ev.Metadata.EventId);
            Version++;
        }
    }

    /// <summary>
    /// Registers the handler invoked when an event of type <typeparamref name="TEvent" /> is
    /// applied — call this from the constructor for every event type the aggregate handles.
    /// Unlike <c>Projector</c>/<c>Reactor</c>'s interface-driven dispatch, this needs no public
    /// handler method and no source generator: <paramref name="handler" /> can be a private
    /// method, and a mismatched signature is still a compile error, from the ordinary generic
    /// delegate conversion <c>On&lt;TEvent&gt;</c> requires.
    ///
    /// If an event of a type with no exact registration is applied, its base types are checked
    /// in turn (most-derived first) — a handler registered for a base event type still catches a
    /// subtype that has no more specific handler of its own.
    /// </summary>
    /// <typeparam name="TEvent">The event type handled.</typeparam>
    /// <param name="handler">Applies the event to the aggregate's mutable state.</param>
    protected void On<TEvent>(Action<TEvent> handler)
        where TEvent : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (!_handlers.TryAdd(typeof(TEvent), ev => handler((TEvent)ev)))
            throw new InvalidOperationException($"A handler for event type '{typeof(TEvent)}' is already registered.");
    }

    /// <summary>
    /// Applies an event to the aggregate's mutable state by dispatching to whichever
    /// <see cref="On{TEvent}" />-registered handler matches — the exact event type first, then
    /// each base type in turn.
    /// </summary>
    /// <param name="ev">The event to apply.</param>
    protected virtual void Apply(DomainEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        for (var type = ev.GetType(); type is not null; type = type.BaseType)
        {
            if (_handlers.TryGetValue(type, out var handler))
            {
                handler(ev);
                return;
            }
        }

        throw new InvalidOperationException($"Aggregate does not handle event type '{ev.GetType()}'.");
    }

    /// <summary>
    /// Applies and records a newly raised state-changing event.
    /// </summary>
    /// <param name="ev">The event to raise.</param>
    protected void RaiseEvent(DomainEvent ev)
    {
        AttachMetadata(ev, checked(Version + 1));
        Apply(ev);
        _uncommittedEvents.Add(ev);
        Version++;
    }

    /// <summary>
    /// Records an audit without changing aggregate state.
    /// </summary>
    /// <param name="ev">The audit event to record.</param>
    protected void AuditEvent(DomainEvent ev)
    {
        AttachMetadata(ev, Version);
        _uncommittedAudits.Add(ev);
    }

    internal void Save()
    {
        _committedEvents.AddRange(_uncommittedEvents);

        foreach (var ev in _uncommittedEvents)
            _ = _committedEventIds.Add(ev.Metadata.EventId);

        _uncommittedEvents.Clear();
        _uncommittedAudits.Clear();
    }

    void AttachMetadata(DomainEvent ev, ulong aggregateVersion)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ev.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion7(),
            Id,
            aggregateVersion,
            DateTimeOffset.UtcNow));
    }

    void ValidateCommittedEvent(DomainEvent ev, ulong expectedVersion, HashSet<Uuid> eventIds)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var metadata = ev.Metadata;

        if (metadata.EventId == Uuid.Empty)
            throw new InvalidOperationException("A committed event ID cannot be empty.");

        if (_committedEventIds.Contains(metadata.EventId) || !eventIds.Add(metadata.EventId))
            throw new InvalidOperationException($"Event ID '{metadata.EventId}' has already been committed.");

        if (metadata.AggregateId != Id)
        {
            throw new InvalidOperationException(
                $"Event aggregate ID '{metadata.AggregateId}' does not match aggregate ID '{Id}'.");
        }

        if (metadata.AggregateVersion != expectedVersion)
        {
            throw new InvalidOperationException(
                $"Event aggregate version '{metadata.AggregateVersion}' does not match expected version '{expectedVersion}'.");
        }
    }
}

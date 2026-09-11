using System.Runtime.InteropServices;

namespace Cntryl.Portia;

/// <summary>
///     Base type for an event-sourced aggregate.
/// </summary>
/// <param name="id">The stable identity of the aggregate.</param>
/// <param name="stream">The stream that stores the aggregate's event history.</param>
/// <param name="metadataFactory">
///     Creates metadata for newly raised events, or
///     <see langword="null" /> to use Portia's system clock and UUID source.
/// </param>
public abstract class Aggregate(
    Uuid id,
    EventStreamAddress stream,
    IDomainEventMetadataFactory? metadataFactory = null)
{
    readonly List<DomainEvent> _committedEvents = [];

    readonly Dictionary<Type, Action<DomainEvent>> _handlers = [];

    // Every event ID this aggregate has attached or replayed, committed or still pending. One
    // set answers both "has this ID been used" for a newly issued event and "was this ID already
    // committed" during replay: an aggregate can only load when it has nothing pending, so the
    // two questions are the same question at the only moment replay asks it.
    readonly HashSet<Uuid> _issuedEventIds = [];

    readonly IDomainEventMetadataFactory
        _metadataFactory = metadataFactory ?? SystemDomainEventMetadataFactory.Instance;

    readonly List<DomainEvent> _uncommittedAudits = [];
    readonly List<DomainEvent> _uncommittedEvents = [];
    EventStreamAddress? _auditSessionStream;
    int _operation;
    EventAttribution? _saveAttribution;
    bool _savePrepared;

    /// <summary>
    ///     Gets the stable identity of the aggregate.
    /// </summary>
    public Uuid Id { get; } = id != Uuid.Empty
        ? id
        : throw new ArgumentException("An aggregate ID cannot be empty.", nameof(id));

    /// <summary>
    ///     Gets the stream that stores the aggregate's event history.
    /// </summary>
    public EventStreamAddress Stream { get; } = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>
    ///     Gets the number of state-changing events applied to the aggregate.
    /// </summary>
    public ulong Version { get; private set; }

    /// <summary>Gets the next physical offset in the raised-event stream. Audit sessions never advance this OCC position.</summary>
    public ulong CommittedStreamPosition { get; private set; }

    internal IReadOnlyList<DomainEvent> CommittedEvents => _committedEvents;

    internal IReadOnlyList<DomainEvent> UncommittedEvents => _uncommittedEvents;

    internal IReadOnlyList<DomainEvent> UncommittedAudits => _uncommittedAudits;

    internal void Load(IReadOnlyList<DomainEvent> committedEvents, ulong? streamPosition = null)
    {
        using var operation = BeginOperation();
        LoadDuringOperation(committedEvents, streamPosition);
    }

    internal void LoadDuringOperation(IReadOnlyList<DomainEvent> committedEvents, ulong? streamPosition = null)
    {
        ArgumentNullException.ThrowIfNull(committedEvents);

        if (committedEvents is List<DomainEvent> list)
        {
            LoadDuringOperation(CollectionsMarshal.AsSpan(list), streamPosition);
            return;
        }

        if (committedEvents is DomainEvent[] array)
        {
            LoadDuringOperation(array.AsSpan(), streamPosition);
            return;
        }

        if (_uncommittedEvents.Count != 0 || _uncommittedAudits.Count != 0)
        {
            throw new InvalidOperationException("An aggregate with uncommitted changes cannot load committed events.");
        }

        var validated = 0;
        try
        {
            for (; validated < committedEvents.Count; validated++)
            {
                var expectedVersion = checked(Version + (ulong)validated + 1);
                ValidateCommittedEvent(committedEvents[validated], expectedVersion);
            }
        }
        catch
        {
            for (var index = 0; index < validated; index++)
                _ = _issuedEventIds.Remove(committedEvents[index].Metadata.EventId);
            throw;
        }

        for (var index = 0; index < committedEvents.Count; index++)
        {
            var ev = committedEvents[index];
            try
            {
                Apply(ev);
            }
            catch
            {
                // Validation reserves the whole batch in the permanent set. Match the historical
                // partial-apply behavior by releasing the failed event and everything after it;
                // successfully applied events stay committed and keep their IDs reserved.
                for (; index < committedEvents.Count; index++)
                    _ = _issuedEventIds.Remove(committedEvents[index].Metadata.EventId);
                throw;
            }

            _committedEvents.Add(ev);
            Version++;
        }

        CommittedStreamPosition = streamPosition ?? checked(CommittedStreamPosition + (ulong)committedEvents.Count);
    }

    void LoadDuringOperation(ReadOnlySpan<DomainEvent> committedEvents, ulong? streamPosition)
    {
        if (_uncommittedEvents.Count != 0 || _uncommittedAudits.Count != 0)
        {
            throw new InvalidOperationException("An aggregate with uncommitted changes cannot load committed events.");
        }

        var validated = 0;
        try
        {
            for (; validated < committedEvents.Length; validated++)
            {
                var expectedVersion = checked(Version + (ulong)validated + 1);
                ValidateCommittedEvent(committedEvents[validated], expectedVersion);
            }
        }
        catch
        {
            for (var index = 0; index < validated; index++)
                _ = _issuedEventIds.Remove(committedEvents[index].Metadata.EventId);
            throw;
        }

        for (var index = 0; index < committedEvents.Length; index++)
        {
            var ev = committedEvents[index];
            try
            {
                Apply(ev);
            }
            catch
            {
                for (; index < committedEvents.Length; index++)
                    _ = _issuedEventIds.Remove(committedEvents[index].Metadata.EventId);
                throw;
            }

            _committedEvents.Add(ev);
            Version++;
        }

        CommittedStreamPosition = streamPosition ?? checked(CommittedStreamPosition + (ulong)committedEvents.Length);
    }

    /// <summary>
    ///     Registers the handler invoked when an event of type <typeparamref name="TEvent" /> is
    ///     applied — call this from the constructor for every event type the aggregate handles.
    ///     Unlike <c>Projector</c>/<c>Reactor</c>'s interface-driven dispatch, this needs no public
    ///     handler method and no source generator: <paramref name="handler" /> can be a private
    ///     method, and a mismatched signature is still a compile error, from the ordinary generic
    ///     delegate conversion <c>On&lt;TEvent&gt;</c> requires.
    ///     If an event of a type with no exact registration is applied, its base types are checked
    ///     in turn (most-derived first) — a handler registered for a base event type still catches a
    ///     subtype that has no more specific handler of its own.
    /// </summary>
    /// <typeparam name="TEvent">The event type handled.</typeparam>
    /// <param name="handler">Applies the event to the aggregate's mutable state.</param>
    protected void On<TEvent>(Action<TEvent> handler)
        where TEvent : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (!_handlers.TryAdd(typeof(TEvent), ev => handler((TEvent)ev)))
        {
            throw new InvalidOperationException($"A handler for event type '{typeof(TEvent)}' is already registered.");
        }
    }

    /// <summary>
    ///     Applies an event to the aggregate's mutable state by dispatching to whichever
    ///     <see cref="On{TEvent}" />-registered handler matches — the exact event type first, then
    ///     each base type in turn.
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
    ///     Applies and records a newly raised state-changing event.
    /// </summary>
    /// <param name="ev">The event to raise.</param>
    protected void RaiseEvent(DomainEvent ev)
    {
        using var operation = BeginOperation();
        if (_savePrepared)
        {
            throw new InvalidOperationException("Resolve the pending save before emitting more events.");
        }

        if (_uncommittedAudits.Count != 0)
        {
            throw new InvalidOperationException("Save pending audits before raising state-changing events.");
        }

        AttachMetadata(ev, checked(Version + 1), false);
        Apply(ev);
        _uncommittedEvents.Add(ev);
        Version++;
    }

    /// <summary>
    ///     Records an audit without changing aggregate state.
    /// </summary>
    /// <param name="ev">The audit event to record.</param>
    protected void AuditEvent(DomainEvent ev)
    {
        using var operation = BeginOperation();
        if (_savePrepared)
        {
            throw new InvalidOperationException("Resolve the pending save before emitting more events.");
        }

        if (_uncommittedEvents.Count != 0)
        {
            throw new InvalidOperationException("Save pending state-changing events before recording audits.");
        }

        AttachMetadata(ev, Version, true);
        _uncommittedAudits.Add(ev);
    }

    internal void PrepareSave(EventAttribution attribution)
    {
        if (_savePrepared && _saveAttribution != attribution)
        {
            throw new InvalidOperationException(
                "Retry a pending save with the original execution context; event attribution is frozen.");
        }

        foreach (var ev in _uncommittedEvents.Concat(_uncommittedAudits))
            ev.ValidateAttribution(attribution);
        if (_savePrepared)
        {
            return;
        }

        foreach (var ev in _uncommittedEvents.Concat(_uncommittedAudits))
            ev.StampAttribution(attribution);
        _saveAttribution = attribution;
        _savePrepared = true;
    }

    internal void Save()
    {
        CommittedStreamPosition = checked(CommittedStreamPosition + (ulong)_uncommittedEvents.Count);
        _committedEvents.AddRange(_uncommittedEvents);
        // Both lists' IDs entered the set when their metadata was attached, so committing them
        // adds nothing new.
        _uncommittedEvents.Clear();
        _uncommittedAudits.Clear();
        _auditSessionStream = null;
        _savePrepared = false;
        _saveAttribution = null;
    }

    internal EventStreamAddress GetAuditSessionStream() =>
        _auditSessionStream ??= new EventStreamAddress(Stream.Realm, Stream.Area, Uuid.CreateVersion4().ToString());

    internal IDisposable BeginOperation()
    {
        // This guards one aggregate operation at a time, and two different situations trip it:
        // a genuinely concurrent call from another thread, and — far more often — a re-entrant
        // one, where an On<TEvent> handler calls RaiseEvent or AuditEvent while the aggregate is
        // still applying the event that ran it. Naming only concurrency sent readers hunting for
        // a second thread that was never there, so the message names both.
        return Interlocked.CompareExchange(ref _operation, 1, 0) != 0
            ? throw new InvalidOperationException(
                "An aggregate operation is already in progress. Either another thread is using this "
                + "aggregate concurrently, or an event handler raised, audited, or saved while applying "
                + "an event — an On<TEvent> handler may only update state, never emit.")
            : (IDisposable)new Operation(this);
    }

    void AttachMetadata(DomainEvent ev, ulong aggregateVersion, bool isAudit)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var metadata = _metadataFactory.Create(Id, aggregateVersion)
                       ?? throw new InvalidOperationException("The event metadata factory returned null.");

        if (metadata.EventId == Uuid.Empty)
        {
            throw new InvalidOperationException("The event metadata factory returned an empty event ID.");
        }

        // Every issued ID is tracked in one set, so this stays a single lookup no matter how many
        // events one command raises. Scanning the two pending lists made a batch of n events cost
        // O(n squared) for a check a hash set already answers.
        if (_issuedEventIds.Contains(metadata.EventId))
        {
            throw new InvalidOperationException(
                $"The event metadata factory returned event ID '{metadata.EventId}', which this aggregate has already used.");
        }

        if (metadata.AggregateId != Id)
        {
            throw new InvalidOperationException("The event metadata factory returned a different aggregate ID.");
        }

        if (metadata.AggregateVersion != aggregateVersion)
        {
            throw new InvalidOperationException("The event metadata factory returned a different aggregate version.");
        }

        if (metadata.OccurredOn.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The event metadata factory returned a non-UTC occurrence time.");
        }

        _ = _issuedEventIds.Add(metadata.EventId);
        ev.AttachAggregateMetadata(metadata with { IsAudit = isAudit });
    }

    void ValidateCommittedEvent(DomainEvent ev, ulong expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var metadata = ev.Metadata;
        if (metadata.IsAudit)
        {
            throw new InvalidOperationException("Aggregate replay accepts only state-changing events.");
        }

        if (metadata.EventId == Uuid.Empty)
        {
            throw new InvalidOperationException("A committed event ID cannot be empty.");
        }

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

        if (!_issuedEventIds.Add(metadata.EventId))
        {
            throw new InvalidOperationException($"Event ID '{metadata.EventId}' has already been committed.");
        }
    }

    sealed class Operation(Aggregate aggregate) : IDisposable
    {
        Aggregate? _aggregate = aggregate;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _aggregate, null);
            if (owner is not null)
            {
                Volatile.Write(ref owner._operation, 0);
            }
        }
    }
}

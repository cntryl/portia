using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Shared execution state and the triggering source event for a reaction.</summary>
public interface IReactorContext : IExecutionContext
{
    /// <summary>Gets the source event and offsets.</summary>
    DomainEventRecord Source { get; }
}

/// <summary>Carries a triggering event and independent system execution authority.</summary>
/// <typeparam name="TEvent">The handled event type.</typeparam>
public interface IReactorContext<out TEvent> : IReactorContext
    where TEvent : DomainEvent
{
    /// <summary>
    /// Gets the triggering event, typed. This is the same instance as
    /// <see cref="IReactorContext.Source" />'s <see cref="DomainEventRecord.Event" />; read this
    /// one for the event itself and <see cref="IReactorContext.Source" /> for its stream offsets.
    /// </summary>
    /// <remarks>Named for its role rather than called <c>Event</c>, which CA1716 reserves on an
    /// interface member because other .NET languages treat it as a keyword.</remarks>
    TEvent Trigger { get; }
}

/// <summary>Execution information created for one event reaction.</summary>
public sealed class ReactionExecutionContext : IReactorContext
{
    /// <summary>Creates a system execution for a triggering event using the receiver's clock.</summary>
    public ReactionExecutionContext(DomainEventRecord source, ClaimsPrincipal actor, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ActorSnapshot = PrincipalSnapshot.Copy(actor);
        if (!RequestActor.IsSystem(ActorSnapshot))
            throw new ArgumentException("Reactors must execute as a system principal, never an end-user principal.", nameof(actor));
        DomainEventValidation.Validate(source.Event);
        Source = source;
        CauseId = source.Event.Metadata.EventId;
        CorrelationId = source.Event.Metadata.CorrelationId ?? CauseId;
        ExecutionId = Uuid.CreateVersion4();
        StartedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    internal ClaimsPrincipal ActorSnapshot { get; }
    /// <inheritdoc />
    public ClaimsPrincipal Actor => PrincipalSnapshot.Copy(ActorSnapshot);
    /// <inheritdoc />
    public Uuid ExecutionId { get; }
    /// <inheritdoc />
    public Uuid CorrelationId { get; }
    /// <inheritdoc />
    public Uuid CauseId { get; }
    /// <inheritdoc />
    public DateTimeOffset StartedAt { get; }
    /// <inheritdoc />
    public DomainEventRecord Source { get; }
}

/// <summary>The typed view of a reaction's execution, supplied by generated event dispatch.</summary>
/// <typeparam name="TEvent">The triggering event type.</typeparam>
public sealed class ReactorContext<TEvent> : IReactorContext<TEvent>
    where TEvent : DomainEvent
{
    readonly IExecutionContext _execution;

    /// <summary>Associates a typed event with the actual source and system execution state.</summary>
    public ReactorContext(TEvent ev, DomainEventRecord source, IExecutionContext execution)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(execution);
        if (!ReferenceEquals(ev, source.Event) || execution.CauseId != source.Event.Metadata.EventId)
            throw new ArgumentException("Reaction context must refer to its actual triggering event.", nameof(source));
        if (!RequestActor.IsSystem(execution.Actor))
            throw new ArgumentException("Reaction execution requires a system principal.", nameof(execution));
        Trigger = ev;
        Source = source;
        _execution = execution;
    }

    /// <inheritdoc />
    public TEvent Trigger { get; }
    /// <inheritdoc />
    public DomainEventRecord Source { get; }
    /// <inheritdoc />
    public ClaimsPrincipal Actor => _execution.Actor;
    /// <inheritdoc />
    public Uuid ExecutionId => _execution.ExecutionId;
    /// <inheritdoc />
    public Uuid CorrelationId => _execution.CorrelationId;
    /// <inheritdoc />
    public Uuid CauseId => _execution.CauseId;
    /// <inheritdoc />
    public DateTimeOffset StartedAt => _execution.StartedAt;
}

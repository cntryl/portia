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
{
    /// <summary>Gets the triggering event.</summary>
    TEvent Ev { get; }
}

/// <summary>Execution information created for one event reaction.</summary>
public sealed class ReactionExecutionContext : IReactorContext
{
    readonly ClaimsPrincipal _actor;

    /// <summary>Creates a system execution for a triggering event using the receiver's clock.</summary>
    public ReactionExecutionContext(DomainEventRecord source, ClaimsPrincipal actor, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _actor = PrincipalSnapshot.Copy(actor);
        if (!RequestActor.IsSystem(_actor))
            throw new ArgumentException("Reactors must execute as a system principal, never an end-user principal.", nameof(actor));
        DomainEventValidation.Validate(source.Ev);
        Source = source;
        CauseId = source.Ev.Metadata.EventId;
        CorrelationId = source.Ev.Metadata.CorrelationId ?? CauseId;
        ExecutionId = Uuid.CreateVersion4();
        StartedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <inheritdoc />
    public ClaimsPrincipal Actor => PrincipalSnapshot.Copy(_actor);
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
{
    readonly IExecutionContext _execution;

    /// <summary>Associates a typed event with the actual source and system execution state.</summary>
    public ReactorContext(TEvent ev, DomainEventRecord source, IExecutionContext execution)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(execution);
        if (!ReferenceEquals(ev, source.Ev) || execution.CauseId != source.Ev.Metadata.EventId)
            throw new ArgumentException("Reaction context must refer to its actual triggering event.", nameof(source));
        if (!RequestActor.IsSystem(execution.Actor))
            throw new ArgumentException("Reaction execution requires a system principal.", nameof(execution));
        Ev = ev;
        Source = source;
        _execution = execution;
    }

    /// <inheritdoc />
    public TEvent Ev { get; }
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

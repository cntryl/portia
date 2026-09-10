using System.Security.Claims;

namespace Cntryl.Portia;

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
        {
            throw new ArgumentException("Reaction context must refer to its actual triggering event.", nameof(source));
        }

        if (!RequestActor.IsSystem(execution.Actor))
        {
            throw new ArgumentException("Reaction execution requires a system principal.", nameof(execution));
        }

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

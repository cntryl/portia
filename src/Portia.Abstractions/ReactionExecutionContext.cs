using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Execution information created for one event reaction.</summary>
public sealed class ReactionExecutionContext : IReactorContext
{
    /// <summary>Creates a system execution for a triggering event using the receiver's clock.</summary>
    public ReactionExecutionContext(DomainEventRecord source, ClaimsPrincipal actor, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ActorSnapshot = PrincipalSnapshot.Copy(actor);
        if (!RequestActor.IsSystem(ActorSnapshot))
        {
            throw new ArgumentException("Reactors must execute as a system principal, never an end-user principal.",
                nameof(actor));
        }

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

using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Execution information created for one event reaction.</summary>
public sealed class ReactionExecutionContext : IReactorContext
{
    /// <summary>Creates a system execution for a triggering event using the receiver's clock.</summary>
    /// <param name="source">The stored record whose event triggers the reaction.</param>
    /// <param name="actor">The system principal the reaction's effects run as.</param>
    /// <param name="timeProvider">
    ///     The clock that stamps the start time, or <see langword="null" /> to use
    ///     <see cref="TimeProvider.System" />.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="actor" /> is not a system principal.</exception>
    public ReactionExecutionContext(DomainEventRecord source, ClaimsPrincipal actor, TimeProvider? timeProvider = null)
        : this(source, timeProvider ?? TimeProvider.System, SnapshotSystemActor(actor))
    {
    }

    ReactionExecutionContext(DomainEventRecord source, TimeProvider timeProvider, ClaimsPrincipal actorSnapshot)
    {
        ArgumentNullException.ThrowIfNull(source);
        ActorSnapshot = actorSnapshot;
        DomainEventValidation.Validate(source.Event);
        Source = source;
        CauseId = source.Event.Metadata.EventId;
        CorrelationId = source.Event.Metadata.CorrelationId ?? CauseId;
        ExecutionId = Uuid.CreateVersion4();
        StartedAt = timeProvider.GetUtcNow();
    }

    internal static ReactionExecutionContext FromSystemSnapshot(DomainEventRecord source,
        ClaimsPrincipal actorSnapshot, TimeProvider timeProvider) => new(source, timeProvider, actorSnapshot);

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

    static ClaimsPrincipal SnapshotSystemActor(ClaimsPrincipal actor)
    {
        var snapshot = PrincipalSnapshot.Copy(actor);
        return RequestActor.IsSystem(snapshot)
            ? snapshot
            : throw new ArgumentException(
                "Reactors must execute as a system principal, never an end-user principal.", nameof(actor));
    }
}

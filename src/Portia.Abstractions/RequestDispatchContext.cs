using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Receiver-created execution state shared by authorization and request handling.</summary>
public class RequestDispatchContext : IRequestContext
{
    /// <summary>Creates a new receiver execution with optional propagated logical identity.</summary>
    /// <param name="actor">
    ///     The actor the request executes as. Never inferred ambiently — pass
    ///     <see cref="RequestActor.Anonymous" /> or <see cref="RequestActor.System" /> explicitly.
    /// </param>
    /// <param name="invocation">
    ///     Facts about the ingress that delivered the request, or
    ///     <see langword="null" /> for a direct in-process invocation.
    /// </param>
    /// <param name="metadata">
    ///     The logical identity to propagate, or <see langword="null" /> to start a
    ///     new logical request.
    /// </param>
    /// <param name="timeProvider">
    ///     The clock that stamps the start time, or <see langword="null" /> to use
    ///     <see cref="TimeProvider.System" />.
    /// </param>
    public RequestDispatchContext(ClaimsPrincipal actor, RequestInvocation? invocation = null,
        RequestMetadata? metadata = null, TimeProvider? timeProvider = null)
    {
        ActorSnapshot = PrincipalSnapshot.Copy(actor);
        Metadata = metadata ?? RequestMetadata.Create();
        Metadata.Validate();
        Invocation = invocation ?? new DirectInvocation();
        ExecutionId = Uuid.CreateVersion4();
        StartedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>Copies an existing execution without generating another identity or start time.</summary>
    /// <param name="context">The execution to copy.</param>
    protected RequestDispatchContext(RequestDispatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The source already holds a snapshot taken at its own construction, and this execution
        // is the same logical one, so its snapshot can be shared rather than copied again. The
        // Actor property still hands every reader an independent copy of it.
        ActorSnapshot = context.ActorSnapshot;
        Metadata = context.Metadata;
        Invocation = context.Invocation;
        ExecutionId = context.ExecutionId;
        StartedAt = context.StartedAt;
    }

    /// <summary>Gets the logical metadata that can be forwarded with this same request.</summary>
    public RequestMetadata Metadata { get; }

    // Portia's own internal reads (null checks, system-identity tests) use this directly: they
    // never hand the principal to application code, so they have nothing to isolate against and
    // no reason to copy it. Everything that does reach application code goes through Actor.
    internal ClaimsPrincipal ActorSnapshot { get; }

    /// <inheritdoc />
    public ClaimsPrincipal Actor => PrincipalSnapshot.Copy(ActorSnapshot);

    /// <inheritdoc />
    public Uuid ExecutionId { get; }

    /// <inheritdoc />
    public Uuid RequestId => Metadata.RequestId;

    /// <inheritdoc />
    public Uuid CorrelationId => Metadata.CorrelationId;

    /// <inheritdoc />
    public Uuid? CausationId => Metadata.CausationId;

    /// <inheritdoc />
    public Uuid CauseId => RequestId;

    /// <inheritdoc />
    public DateTimeOffset StartedAt { get; }

    /// <inheritdoc />
    public RequestInvocation Invocation { get; }
}

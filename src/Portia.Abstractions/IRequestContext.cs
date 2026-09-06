using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Describes one execution of a logical request.</summary>
public interface IRequestContext : IExecutionContext
{
    /// <summary>Gets the logical request identity, preserved by propagated redeliveries.</summary>
    Uuid RequestId { get; }
    /// <summary>Gets the logical request or event that caused this request.</summary>
    Uuid? CausationId { get; }
    /// <summary>Gets factual information about this execution's immediate ingress.</summary>
    RequestInvocation Invocation { get; }
}

/// <summary>Carries typed business input and its execution context.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestContext<out TRequest> : IRequestContext
{
    /// <summary>Gets the business input.</summary>
    TRequest Request { get; }
}

/// <summary>Receiver-created execution state shared by authorization and request handling.</summary>
public class RequestDispatchContext : IRequestContext
{
    readonly ClaimsPrincipal _actor;

    /// <summary>Creates a new receiver execution with optional propagated logical identity.</summary>
    public RequestDispatchContext(ClaimsPrincipal actor, RequestInvocation? invocation = null,
        RequestMetadata? metadata = null, TimeProvider? timeProvider = null)
    {
        _actor = PrincipalSnapshot.Copy(actor);
        Metadata = metadata ?? RequestMetadata.Create();
        Metadata.Validate();
        Invocation = invocation ?? new DirectInvocation();
        ExecutionId = Uuid.CreateVersion4();
        StartedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>Copies an existing execution without generating another identity or start time.</summary>
    protected RequestDispatchContext(RequestDispatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _actor = PrincipalSnapshot.Copy(context._actor);
        Metadata = context.Metadata;
        Invocation = context.Invocation;
        ExecutionId = context.ExecutionId;
        StartedAt = context.StartedAt;
    }

    /// <summary>Gets the logical metadata that can be forwarded with this same request.</summary>
    public RequestMetadata Metadata { get; }
    /// <inheritdoc />
    public ClaimsPrincipal Actor => PrincipalSnapshot.Copy(_actor);
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

/// <summary>The typed view of an execution supplied to a handler or authorizer.</summary>
/// <typeparam name="TRequest">The business input type.</typeparam>
public sealed class RequestContext<TRequest> : RequestDispatchContext, IRequestContext<TRequest>
{
    /// <summary>Creates an anonymous direct context for existing manually invoked handlers.</summary>
    public RequestContext(TRequest request) : this(request, RequestActor.Anonymous) { }

    /// <summary>Creates a direct context with an explicit actor.</summary>
    public RequestContext(TRequest request, ClaimsPrincipal actor) : this(request, new RequestDispatchContext(actor)) { }

    /// <summary>Associates a request with already created receiver execution state.</summary>
    public RequestContext(TRequest request, RequestDispatchContext context) : base(context)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    /// <inheritdoc />
    public TRequest Request { get; }
}

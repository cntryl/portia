using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>The typed view of an execution supplied to a handler or authorizer.</summary>
/// <typeparam name="TRequest">The business input type.</typeparam>
public sealed class RequestContext<TRequest> : RequestDispatchContext, IRequestContext<TRequest>
{
    /// <summary>Creates an anonymous direct context for existing manually invoked handlers.</summary>
    public RequestContext(TRequest request) : this(request, RequestActor.Anonymous)
    {
    }

    /// <summary>Creates a direct context with an explicit actor.</summary>
    public RequestContext(TRequest request, ClaimsPrincipal actor) : this(request, new RequestDispatchContext(actor))
    {
    }

    /// <summary>Associates a request with already created receiver execution state.</summary>
    public RequestContext(TRequest request, RequestDispatchContext context) : base(context)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    /// <inheritdoc />
    public TRequest Request { get; }
}

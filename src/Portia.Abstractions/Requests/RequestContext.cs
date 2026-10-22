using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>The typed view of an execution supplied to a handler or authorizer.</summary>
/// <typeparam name="TRequest">The business input type.</typeparam>
public sealed class RequestContext<TRequest> : RequestDispatchContext, IRequestContext<TRequest>
{
    /// <summary>Creates an anonymous direct context for existing manually invoked handlers.</summary>
    /// <param name="request">The business input handed to the handler.</param>
    public RequestContext(TRequest request) : this(request, RequestActor.Anonymous)
    {
    }

    /// <summary>Creates a direct context with an explicit actor.</summary>
    /// <param name="request">The business input handed to the handler.</param>
    /// <param name="actor">The actor the request executes as.</param>
    public RequestContext(TRequest request, ClaimsPrincipal actor) : this(request, CreateExecution(request, actor))
    {
    }

    /// <summary>Associates a request with already created receiver execution state.</summary>
    /// <param name="request">The business input handed to the handler.</param>
    /// <param name="context">The execution state this request runs under.</param>
    public RequestContext(TRequest request, RequestDispatchContext context) : base(context)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    /// <inheritdoc />
    public TRequest Request { get; }

    static RequestDispatchContext CreateExecution(TRequest request, ClaimsPrincipal actor)
    {
        var context = new RequestDispatchContext(actor);
        if (request is not IRequestBase command)
            return context;
        var bound = context.BindRequest(command);
        return bound.IsSuccess ? bound.Value : throw new ArgumentException(bound.Error.Message, nameof(request));
    }
}

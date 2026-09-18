using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cntryl.Portia;

/// <summary>Shared generated-endpoint configuration infrastructure.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class PortiaEndpointConfigurationBase<TRequest, TConfiguration>
    where TConfiguration : PortiaEndpointConfigurationBase<TRequest, TConfiguration>
{
    readonly PortiaEndpointConfigurationState<TRequest> _state = new();

    private protected PortiaEndpointConfigurationBase()
    {
    }

    /// <summary>Gets the binder used by generated endpoint code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Func<HttpContext, CancellationToken, ValueTask<TRequest>>? Binder => _state.Binder;

    /// <summary>Gets whether a custom request body was declared.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool HasBody => _state.HasBody;

    /// <summary>Uses a synchronous custom request binder.</summary>
    public TConfiguration OnBind(Func<HttpContext, TRequest> binder)
    {
        _state.OnBind(binder);
        return (TConfiguration)this;
    }

    /// <summary>Uses an asynchronous custom request binder.</summary>
    public TConfiguration OnBind(Func<HttpContext, CancellationToken, ValueTask<TRequest>> binder)
    {
        _state.OnBind(binder);
        return (TConfiguration)this;
    }

    /// <summary>Declares the custom-bound request body.</summary>
    public TConfiguration Accepts<TBody>(string contentType, bool isOptional = false,
        params string[] additionalContentTypes)
    {
        _state.Accepts<TBody>(contentType, isOptional, additionalContentTypes);
        return (TConfiguration)this;
    }

    /// <summary>Declares a custom-bound non-body request value.</summary>
    public TConfiguration Parameter<T>(string name, PortiaHttpParameterLocation location, bool required = true)
    {
        _state.Parameter<T>(name, location, required);
        return (TConfiguration)this;
    }

    /// <summary>
    ///     Binds a request member from the query string instead of the request body, and describes it
    ///     as a query parameter in OpenAPI. The member must be a string, enum, or <c>TryParse</c> scalar.
    /// </summary>
    /// <typeparam name="TMember">The member's type.</typeparam>
    /// <param name="member">The request member, such as <c>x =&gt; x.DryRun</c>.</param>
    /// <returns>This configuration.</returns>
    public TConfiguration FromQuery<TMember>(Expression<Func<TRequest, TMember>> member)
    {
        _state.FromQuery(MemberName(member));
        return (TConfiguration)this;
    }

    /// <summary>Declares that the custom binder consumes no request input.</summary>
    public TConfiguration NoInput()
    {
        _state.NoInput();
        return (TConfiguration)this;
    }

    /// <summary>Declares a response without a body.</summary>
    public TConfiguration Produces(int statusCode)
    {
        _state.Produces(statusCode);
        return (TConfiguration)this;
    }

    /// <summary>Declares a typed response body.</summary>
    public TConfiguration Produces<TBody>(int statusCode, string contentType = "application/json")
    {
        _state.Produces<TBody>(statusCode, contentType);
        return (TConfiguration)this;
    }

    /// <summary>Validates state for generated endpoint code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Validate(bool binderRequired = false, PortiaHttpMember[]? members = null) =>
        _state.Validate(binderRequired, members ?? []);

    /// <summary>Reports whether generated binding reads a request member only from the query string.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool IsQueryMember(int index) => _state.IsQueryMember(index);

    /// <summary>Applies HTTP contract metadata.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ApplyMetadata(IEndpointRouteBuilder app, IEndpointConventionBuilder builder) =>
        _state.ApplyMetadata(app, builder);

    /// <summary>Rejects mutation after endpoint mapping.</summary>
    private protected void EnsureMutable() => _state.EnsureMutable();

    private protected void RegisterResultHandler() => _state.RegisterResultHandler();

    private protected IResult? EnsureResultHandlerResponse(bool success, IResult? response) =>
        _state.EnsureResultHandlerResponse(success, response);

    // The selector is inspected, never compiled, so it stays trimming- and Native AOT-safe.
    static string MemberName<TMember>(Expression<Func<TRequest, TMember>> member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var body = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : member.Body;
        return body is MemberExpression { Member: PropertyInfo or FieldInfo } access &&
               access.Expression == member.Parameters[0]
            ? access.Member.Name
            : throw new ArgumentException("FromQuery requires a direct request member, such as x => x.Name.",
                nameof(member));
    }
}

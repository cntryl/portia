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
    public TConfiguration Parameter<T>(string name, PortiaHttpParameterLocation location, bool required = true,
        string? description = null)
    {
        _state.Parameter<T>(name, location, required, description);
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
        _state.FromQuery(MemberName(member, nameof(FromQuery)));
        return (TConfiguration)this;
    }

    /// <summary>
    ///     Binds a request member from one HTTP header and describes that header in OpenAPI.
    ///     The member must be a scalar value supported by Portia's text binder.
    /// </summary>
    /// <typeparam name="TMember">The member's type.</typeparam>
    /// <param name="member">The request member, such as <c>x =&gt; x.TenantId</c>.</param>
    /// <param name="name">The HTTP header name, such as <c>Tenant-Id</c>.</param>
    /// <returns>This configuration.</returns>
    public TConfiguration FromHeader<TMember>(Expression<Func<TRequest, TMember>> member, string name)
    {
        _state.FromHeader(MemberName(member, nameof(FromHeader)), name);
        return (TConfiguration)this;
    }

    /// <summary>Describes a constructor-bound request member at this endpoint.</summary>
    public TConfiguration Describe<TMember>(Expression<Func<TRequest, TMember>> member, string description)
    {
        _state.Describe(PortiaOpenApiMemberSelector.Name(member), description);
        return (TConfiguration)this;
    }

    /// <summary>Describes the single success response; use a status code when multiple success responses exist.</summary>
    public TConfiguration DescribeResponse(string description)
    {
        _state.DescribeResponse(null, description);
        return (TConfiguration)this;
    }

    /// <summary>Describes an emitted response with the specified HTTP status code.</summary>
    public TConfiguration DescribeResponse(int statusCode, string description)
    {
        _state.DescribeResponse(statusCode, description);
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

    /// <summary>Adds a typed input example; generated binding projects route and query values out of the body.</summary>
    public TConfiguration RequestExample<TBody>(string name, TBody value, string? summary = null,
        string contentType = "application/json") where TBody : notnull
    {
        _state.Example(name, value, summary, contentType, null);
        return (TConfiguration)this;
    }

    /// <summary>Adds a typed example to an existing response status and content type.</summary>
    public TConfiguration ResponseExample<TBody>(int statusCode, string name, TBody value, string? summary = null,
        string contentType = "application/json") where TBody : notnull
    {
        _state.Example(name, value, summary, contentType, statusCode);
        return (TConfiguration)this;
    }

    /// <summary>Describes a typed header on an existing response status.</summary>
    public TConfiguration ResponseHeader<T>(int statusCode, string name, string description, bool required = false)
    {
        _state.ResponseHeader<T>(statusCode, name, description, required);
        return (TConfiguration)this;
    }

    /// <summary>Validates state for generated endpoint code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Validate(bool binderRequired = false, PortiaHttpMember[]? members = null) =>
        _state.Validate(binderRequired, members ?? []);

    /// <summary>Configures an HTTP create response for a typed create outcome.</summary>
    private protected void ConfigureCreated(string uriTemplate, Type? payloadType) =>
        _state.Created(uriTemplate, payloadType);

    /// <summary>Reports whether generated binding reads a request member only from the query string.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool IsQueryMember(int index) => _state.IsQueryMember(index);

    /// <summary>Reports whether generated binding reads a request member from an HTTP header.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool IsHeaderMember(int index) => _state.IsHeaderMember(index);

    /// <summary>Gets the configured header name for a generated request member.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? HeaderName(int index) => _state.HeaderName(index);

    /// <summary>Applies HTTP contract metadata.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ApplyMetadata(IEndpointRouteBuilder app, IEndpointConventionBuilder builder) =>
        _state.ApplyMetadata(app, builder);

    /// <summary>Applies HTTP metadata with generated accessors for create URI placeholders.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ApplyMetadata(IEndpointRouteBuilder app, IEndpointConventionBuilder builder,
        IReadOnlyDictionary<string, Func<TRequest, object?>> requestMembers,
        IReadOnlyDictionary<string, Func<object?, object?>>? payloadMembers, bool createPayloadIsNullable) =>
        _state.ApplyMetadata(app, builder, requestMembers, payloadMembers, createPayloadIsNullable);

    /// <summary>Rejects mutation after endpoint mapping.</summary>
    private protected void EnsureMutable() => _state.EnsureMutable();

    private protected void RegisterResultHandler() => _state.RegisterResultHandler();

    private protected IResult? EnsureResultHandlerResponse(bool success, IResult? response) =>
        _state.EnsureResultHandlerResponse(success, response);

    // The selector is inspected, never compiled, so it stays trimming- and Native AOT-safe.
    static string MemberName<TMember>(Expression<Func<TRequest, TMember>> member, string source)
    {
        ArgumentNullException.ThrowIfNull(member);
        var body = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : member.Body;
        return body is MemberExpression { Member: PropertyInfo or FieldInfo } access &&
               access.Expression == member.Parameters[0]
            ? access.Member.Name
            : throw new ArgumentException($"{source} requires a direct request member, such as x => x.Name.",
                nameof(member));
    }
}

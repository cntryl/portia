using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Identifies where a custom-bound value appears in an HTTP request.</summary>
public enum PortiaHttpParameterLocation
{
    /// <summary>A route parameter.</summary>
    Route,
    /// <summary>A query-string parameter.</summary>
    Query,
    /// <summary>An HTTP header.</summary>
    Header,
    /// <summary>An HTTP cookie.</summary>
    Cookie
}

/// <summary>Shared generated-endpoint configuration infrastructure.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class PortiaEndpointConfigurationBase<TRequest, TConfiguration>
    where TConfiguration : PortiaEndpointConfigurationBase<TRequest, TConfiguration>
{
    readonly PortiaEndpointConfigurationState<TRequest> _state = new();
    private protected PortiaEndpointConfigurationBase() { }
    /// <summary>Gets the binder used by generated endpoint code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)] public Func<HttpContext, CancellationToken, ValueTask<TRequest>>? Binder => _state.Binder;
    /// <summary>Gets whether a custom request body was declared.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)] public bool HasBody => _state.HasBody;
    /// <summary>Uses a synchronous custom request binder.</summary>
    public TConfiguration OnBind(Func<HttpContext, TRequest> binder) { _state.OnBind(binder); return (TConfiguration)this; }
    /// <summary>Uses an asynchronous custom request binder.</summary>
    public TConfiguration OnBind(Func<HttpContext, CancellationToken, ValueTask<TRequest>> binder) { _state.OnBind(binder); return (TConfiguration)this; }
    /// <summary>Declares the custom-bound request body.</summary>
    public TConfiguration Accepts<TBody>(string contentType, bool isOptional = false, params string[] additionalContentTypes) { _state.Accepts<TBody>(contentType, isOptional, additionalContentTypes); return (TConfiguration)this; }
    /// <summary>Declares a custom-bound non-body request value.</summary>
    public TConfiguration Parameter<T>(string name, PortiaHttpParameterLocation location, bool required = true) { _state.Parameter<T>(name, location, required); return (TConfiguration)this; }
    /// <summary>
    ///     Binds a request member from the query string instead of the request body, and describes it
    ///     as a query parameter in OpenAPI. The member must be a string, enum, or <c>TryParse</c> scalar.
    /// </summary>
    /// <typeparam name="TMember">The member's type.</typeparam>
    /// <param name="member">The request member, such as <c>x =&gt; x.DryRun</c>.</param>
    /// <returns>This configuration.</returns>
    public TConfiguration FromQuery<TMember>(Expression<Func<TRequest, TMember>> member) { _state.FromQuery(MemberName(member)); return (TConfiguration)this; }
    /// <summary>Declares that the custom binder consumes no request input.</summary>
    public TConfiguration NoInput() { _state.NoInput(); return (TConfiguration)this; }
    /// <summary>Declares a response without a body.</summary>
    public TConfiguration Produces(int statusCode) { _state.Produces(statusCode); return (TConfiguration)this; }
    /// <summary>Declares a typed response body.</summary>
    public TConfiguration Produces<TBody>(int statusCode, string contentType = "application/json") { _state.Produces<TBody>(statusCode, contentType); return (TConfiguration)this; }
    /// <summary>Validates state for generated endpoint code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)] public void Validate(bool binderRequired = false, PortiaHttpMember[]? members = null) => _state.Validate(binderRequired, members ?? []);
    /// <summary>Reports whether generated binding reads a request member only from the query string.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)] public bool IsQueryMember(int index) => _state.IsQueryMember(index);
    /// <summary>Applies HTTP contract metadata.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)] public void ApplyMetadata(IEndpointRouteBuilder app, IEndpointConventionBuilder builder) => _state.ApplyMetadata(app, builder);
    /// <summary>Rejects mutation after endpoint mapping.</summary>
    private protected void EnsureMutable() => _state.EnsureMutable();
    private protected void RegisterResultHandler() => _state.RegisterResultHandler();
    private protected IResult? EnsureResultHandlerResponse(bool success, IResult? response) =>
        _state.EnsureResultHandlerResponse(success, response);

    // The selector is inspected, never compiled, so it stays trimming- and Native AOT-safe.
    static string MemberName<TMember>(Expression<Func<TRequest, TMember>> member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var body = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : member.Body;
        return body is MemberExpression { Member: PropertyInfo or FieldInfo } access && access.Expression == member.Parameters[0]
            ? access.Member.Name
            : throw new ArgumentException("FromQuery requires a direct request member, such as x => x.Name.", nameof(member));
    }
}

/// <summary>Configures a no-result Portia HTTP endpoint.</summary>
public sealed class PortiaEndpointConfiguration<TRequest>
    : PortiaEndpointConfigurationBase<TRequest, PortiaEndpointConfiguration<TRequest>>
{
    /// <summary>Gets the result hook used by generated endpoint code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)] public Func<HttpContext, Result, CancellationToken, ValueTask<IResult?>>? ResultHandler { get; private set; }
    /// <summary>Optionally replaces the final synchronous Portia response. Declared success responses replace Portia's default success response in OpenAPI.</summary>
    public PortiaEndpointConfiguration<TRequest> OnResult(Func<HttpContext, Result, IResult?> handler) { ArgumentNullException.ThrowIfNull(handler); SetResult((http, result, _) => ValueTask.FromResult(handler(http, result))); return this; }
    /// <summary>Asynchronously and optionally replaces the final synchronous Portia response. Declared success responses replace Portia's default success response in OpenAPI.</summary>
    public PortiaEndpointConfiguration<TRequest> OnResult(Func<HttpContext, Result, CancellationToken, ValueTask<IResult?>> handler) { ArgumentNullException.ThrowIfNull(handler); SetResult(handler); return this; }
    void SetResult(Func<HttpContext, Result, CancellationToken, ValueTask<IResult?>> handler) { EnsureMutable(); if (ResultHandler is not null) throw new InvalidOperationException("OnResult can only be configured once."); RegisterResultHandler(); ResultHandler = async (http, result, ct) => EnsureResultHandlerResponse(result.IsSuccess, await handler(http, result, ct).ConfigureAwait(false)); }
}

/// <summary>Configures a streaming Portia HTTP endpoint.</summary>
public sealed class PortiaStreamingEndpointConfiguration<TRequest>
    : PortiaEndpointConfigurationBase<TRequest, PortiaStreamingEndpointConfiguration<TRequest>>;

/// <summary>Configures a result-bearing Portia HTTP endpoint.</summary>
public sealed class PortiaEndpointConfiguration<TRequest, TOut>
    : PortiaEndpointConfigurationBase<TRequest, PortiaEndpointConfiguration<TRequest, TOut>>
{
    /// <summary>Gets the result hook used by generated endpoint code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)] public Func<HttpContext, Result<TOut>, CancellationToken, ValueTask<IResult?>>? ResultHandler { get; private set; }
    /// <summary>Optionally replaces the final synchronous Portia response. Declared success responses replace Portia's default success response in OpenAPI.</summary>
    public PortiaEndpointConfiguration<TRequest, TOut> OnResult(Func<HttpContext, Result<TOut>, IResult?> handler) { ArgumentNullException.ThrowIfNull(handler); SetResult((http, result, _) => ValueTask.FromResult(handler(http, result))); return this; }
    /// <summary>Asynchronously and optionally replaces the final synchronous Portia response. Declared success responses replace Portia's default success response in OpenAPI.</summary>
    public PortiaEndpointConfiguration<TRequest, TOut> OnResult(Func<HttpContext, Result<TOut>, CancellationToken, ValueTask<IResult?>> handler) { ArgumentNullException.ThrowIfNull(handler); SetResult(handler); return this; }
    void SetResult(Func<HttpContext, Result<TOut>, CancellationToken, ValueTask<IResult?>> handler) { EnsureMutable(); if (ResultHandler is not null) throw new InvalidOperationException("OnResult can only be configured once."); RegisterResultHandler(); ResultHandler = async (http, result, ct) => EnsureResultHandlerResponse(result.IsSuccess, await handler(http, result, ct).ConfigureAwait(false)); }
}

sealed class PortiaEndpointConfigurationState<TRequest>
{
    readonly List<PortiaHttpContractParameter> _parameters = [];
    readonly List<object> _responses = [];
    readonly List<string> _queryMembers = [];
    bool[] _queryMemberFlags = [];
    bool _noInput;
    bool _frozen;
    bool _hasResultHandler;
    public Func<HttpContext, CancellationToken, ValueTask<TRequest>>? Binder { get; private set; }
    Type? BodyType { get; set; }
    string[]? BodyContentTypes { get; set; }
    bool BodyOptional { get; set; }
    public bool HasBody => BodyType is not null;
    public void OnBind(Func<HttpContext, TRequest> binder) { ArgumentNullException.ThrowIfNull(binder); SetBinder((http, _) => ValueTask.FromResult(binder(http))); }
    public void OnBind(Func<HttpContext, CancellationToken, ValueTask<TRequest>> binder) { ArgumentNullException.ThrowIfNull(binder); SetBinder(binder); }
    public void Accepts<TBody>(string contentType, bool optional, string[] additional)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(additional);
        EnsureInput();
        if (BodyType is not null)
            throw new InvalidOperationException("Only one custom request body can be declared.");
        var types = new[] { contentType }.Concat(additional).ToArray();
        if (types.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Content types cannot be empty.", nameof(additional));
        if (types.Distinct(StringComparer.OrdinalIgnoreCase).Count() != types.Length)
            throw new InvalidOperationException("Custom request body content types must be unique.");
        BodyType = typeof(TBody);
        BodyOptional = optional;
        BodyContentTypes = types;
    }
    public void Parameter<T>(string name, PortiaHttpParameterLocation location, bool required)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureInput();
        if (_parameters.Any(item => item.Location == location && StringComparer.OrdinalIgnoreCase.Equals(item.Name, name)))
            throw new InvalidOperationException($"The custom {location} parameter '{name}' is declared more than once.");
        _parameters.Add(new(name, typeof(T), location, required));
    }
    public void FromQuery(string member)
    {
        EnsureMutable();
        if (_queryMembers.Contains(member, StringComparer.Ordinal))
            throw new InvalidOperationException($"FromQuery is declared more than once for '{member}'.");
        _queryMembers.Add(member);
    }
    public bool IsQueryMember(int index) => (uint)index < (uint)_queryMemberFlags.Length && _queryMemberFlags[index];
    public void NoInput() { EnsureMutable(); if (BodyType is not null || _parameters.Count != 0) throw new InvalidOperationException("NoInput cannot be combined with custom input declarations."); _noInput = true; }
    public void Produces(int status) { EnsureMutable(); _responses.Add(new ProducesResponseTypeMetadata(status, null, [])); }
    public void Produces<TBody>(int status, string contentType)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        _responses.Add(typeof(Stream).IsAssignableFrom(typeof(TBody))
            ? new PortiaStreamProducesMetadata(status, typeof(TBody), [contentType])
            : new ProducesResponseTypeMetadata(status, typeof(TBody), [contentType]));
    }
    public void RegisterResultHandler() { EnsureMutable(); _hasResultHandler = true; }
    public IResult? EnsureResultHandlerResponse(bool success, IResult? response)
    {
        if (success && response is null && _responses.Any(item => ResponseStatus(item) is >= 200 and < 400))
            throw new InvalidOperationException(
                "OnResult must return a response for a successful result when a custom success response is declared.");
        return response;
    }
    public void Validate(bool binderRequired, PortiaHttpMember[] members)
    {
        if (_queryMembers.Count != 0 && Binder is not null)
            throw new InvalidOperationException("FromQuery applies to default binding; declare OnBind inputs with Parameter.");
        var flags = new bool[members.Length];
        foreach (var name in _queryMembers)
        {
            var index = Array.FindIndex(members, item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new InvalidOperationException($"FromQuery member '{name}' is not a constructor-bound request member.");
            if (members[index].Source == "route")
                throw new InvalidOperationException($"FromQuery member '{name}' is already bound from the route.");
            if (!members[index].Scalar)
                throw new InvalidOperationException($"FromQuery member '{name}' must be a string, enum, or TryParse scalar.");
            flags[index] = true;
        }
        _queryMemberFlags = flags;
        if (binderRequired && Binder is null)
            throw new InvalidOperationException("This request shape requires OnBind.");
        if (Binder is null && (_noInput || BodyType is not null || _parameters.Count != 0))
            throw new InvalidOperationException("Custom input declarations require OnBind.");
        if (Binder is not null && !_noInput && BodyType is null && _parameters.Count == 0)
            throw new InvalidOperationException("OnBind must declare its inputs with Accepts, Parameter, or NoInput.");
        _frozen = true;
    }
    public void ApplyMetadata(IEndpointRouteBuilder app, IEndpointConventionBuilder builder)
    {
        var validations = app.ServiceProvider.GetRequiredService<PortiaStartupValidationRegistry>();
        // RouteGroupBuilder.DataSources contains the group's unprefixed child sources. Building
        // those directly both loses ancestor route parameters and caches the wrong endpoint shape;
        // ASP.NET builds them through the root data source with the complete RouteGroupContext.
        validations.Add("http-endpoints", () =>
        {
            if (app is RouteGroupBuilder)
            {
                // Prime the root composite so ASP.NET's later attachment validates the complete
                // grouped pattern; the endpoint-validation gate below holds Portia workers until then.
                foreach (var source in app.ServiceProvider.GetServices<EndpointDataSource>())
                    _ = source.Endpoints;
                return;
            }

            foreach (var source in app.DataSources)
                _ = source.Endpoints;
        });
        if (BodyType is not null)
        {
            if (typeof(Stream).IsAssignableFrom(BodyType))
            {
                builder.Add(endpoint => endpoint.Metadata.Add(
                    new PortiaStreamAcceptsMetadata(BodyContentTypes!, BodyType, BodyOptional)));
            }
            else
            {
                builder.Add(endpoint => endpoint.Metadata.Add(
                    new AcceptsMetadata(BodyContentTypes!, BodyType, BodyOptional)));
            }
        }
        foreach (var response in _responses)
            builder.Add(endpoint => endpoint.Metadata.Add(response));
        if (_queryMembers.Count != 0)
            builder.Add(endpoint => endpoint.Metadata.Add(new PortiaQueryMembers([.. _queryMembers])));
        if (_hasResultHandler)
            builder.Add(endpoint => endpoint.Metadata.Add(new PortiaCustomHttpResult(
                [.. _responses.Select(ResponseStatus)])));
        if (Binder is null)
            return;
        var endpointValidation = validations.BeginEndpointValidation();
        builder.Add(endpoint =>
        {
            var route = (endpoint as RouteEndpointBuilder)?.RoutePattern ?? throw new InvalidOperationException("Portia HTTP endpoints require route metadata.");
            var routeNames = route.Parameters.Select(item => item.Name).ToArray();
            var declared = _parameters.Where(item => item.Location == PortiaHttpParameterLocation.Route).Select(item => item.Name).ToArray();
            if (routeNames.Length != declared.Length || routeNames.Any(name => !declared.Contains(name, StringComparer.Ordinal)))
                throw new InvalidOperationException("Custom route parameters must exactly match the complete route pattern, including route groups and casing.");
            endpoint.Metadata.Add(new PortiaCustomHttpContract([.. _parameters]));
        });
        builder.Finally(_ => endpointValidation.Complete());
    }
    void SetBinder(Func<HttpContext, CancellationToken, ValueTask<TRequest>> binder) { EnsureMutable(); if (Binder is not null) throw new InvalidOperationException("OnBind can only be configured once."); Binder = binder; }
    void EnsureInput() { if (_noInput) throw new InvalidOperationException("Custom input declarations cannot be combined with NoInput."); }
    static int ResponseStatus(object response) => response switch
    {
        IProducesResponseTypeMetadata metadata => metadata.StatusCode,
        PortiaStreamProducesMetadata metadata => metadata.StatusCode,
        _ => throw new InvalidOperationException("Unknown Portia response metadata.")
    };
    public void EnsureMutable()
    {
        if (_frozen)
            throw new InvalidOperationException("Endpoint configuration cannot be changed after mapping.");
    }
}

sealed record PortiaHttpContractParameter(string Name, Type Type, PortiaHttpParameterLocation Location, bool Required);
sealed record PortiaCustomHttpContract(IReadOnlyList<PortiaHttpContractParameter> Parameters);
sealed record PortiaQueryMembers(IReadOnlyList<string> Names);
sealed record PortiaCustomHttpResult(IReadOnlyList<int> DeclaredStatusCodes);
sealed record PortiaStreamAcceptsMetadata(IReadOnlyList<string> ContentTypes, Type RequestType, bool IsOptional);
sealed record PortiaStreamProducesMetadata(int StatusCode, Type Type, IReadOnlyList<string> ContentTypes);

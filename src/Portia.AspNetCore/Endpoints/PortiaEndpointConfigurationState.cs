using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

sealed class PortiaEndpointConfigurationState<TRequest>
{
    readonly List<PortiaHttpContractParameter> _parameters = [];
    readonly List<string> _queryMembers = [];
    readonly Dictionary<string, string> _headerMembers = [with(StringComparer.OrdinalIgnoreCase)];
    readonly List<object> _responses = [];
    readonly Dictionary<string, string> _descriptions = [with(StringComparer.OrdinalIgnoreCase)];
    readonly Dictionary<int, string> _responseDescriptions = [];
    readonly List<PortiaHttpExample> _requestExamples = [];
    readonly List<PortiaHttpExample> _responseExamples = [];
    readonly List<PortiaHttpResponseHeader> _responseHeaders = [];
    string? _successDescription;
    bool _frozen;
    bool _hasResultHandler;
    (string Template, Type? PayloadType)? _created;
    bool _noInput;
    public Func<HttpContext, CancellationToken, ValueTask<TRequest>>? Binder { get; private set; }
    Type? BodyType { get; set; }
    string[]? BodyContentTypes { get; set; }
    bool BodyOptional { get; set; }
    public bool HasBody => BodyType is not null;

    public void OnBind(Func<HttpContext, TRequest> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        SetBinder((http, _) => ValueTask.FromResult(binder(http)));
    }

    public void OnBind(Func<HttpContext, CancellationToken, ValueTask<TRequest>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        SetBinder(binder);
    }

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

    public void Parameter<T>(string name, PortiaHttpParameterLocation location, bool required, string? description)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureInput();
        if (_parameters.Any(item =>
                item.Location == location && StringComparer.OrdinalIgnoreCase.Equals(item.Name, name)))
            throw new InvalidOperationException(
                $"The custom {location} parameter '{name}' is declared more than once.");
        if (description is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(description);
        _parameters.Add(new PortiaHttpContractParameter(name, typeof(T), location, required, description));
    }

    public void Describe(string member, string description)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!_descriptions.TryAdd(member, description))
            throw new InvalidOperationException($"OpenAPI member '{member}' is described more than once.");
    }

    public void DescribeResponse(int? status, string description)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (status is { } code)
        {
            if (code is < 100 or > 599)
                throw new ArgumentOutOfRangeException(nameof(status));
            if (!_responseDescriptions.TryAdd(code, description))
                throw new InvalidOperationException($"OpenAPI response '{code}' is described more than once.");
        }
        else
        {
            if (_successDescription is not null)
                throw new InvalidOperationException("OpenAPI success response is described more than once.");
            _successDescription = description;
        }
    }

    public void FromQuery(string member)
    {
        EnsureMutable();
        if (_queryMembers.Contains(member, StringComparer.Ordinal))
            throw new InvalidOperationException($"FromQuery is declared more than once for '{member}'.");
        _queryMembers.Add(member);
    }

    public void FromHeader(string member, string name)
    {
        EnsureMutable();
        PortiaHttpHeaderName.Validate(name);
        if (_headerMembers.ContainsKey(member))
            throw new InvalidOperationException($"FromHeader is declared more than once for '{member}'.");
        if (_headerMembers.Values.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"HTTP header '{name}' is declared more than once.");
        _headerMembers.Add(member, name);
    }

    public void Example<T>(string name, T value, string? summary, string contentType, int? status) where T : notnull
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (status is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(status));
        var examples = status.HasValue ? _responseExamples : _requestExamples;
        if (examples.Any(item => item.Example.Name == name && item.Status == status &&
                                 string.Equals(item.ContentType, contentType, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"HTTP example '{name}' is declared more than once.");
        examples.Add(new PortiaHttpExample(new PortiaContractExample(typeof(T), name, value, summary,
            status.HasValue ? PortiaContractUse.Output : PortiaContractUse.Input), contentType, status));
    }

    public void ResponseHeader<T>(int status, string name, string description, bool required)
    {
        EnsureMutable();
        if (status is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (_responseHeaders.Any(item =>
                item.Status == status && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Response header '{name}' is declared more than once for status {status}.");
        _responseHeaders.Add(new PortiaHttpResponseHeader(status, name, typeof(T), description, required));
    }

    public PortiaHttpMemberSources ResolveMemberSources(IEndpointRouteBuilder app, PortiaHttpMember[] members)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(members);
        return Binder is not null
            ? PortiaHttpMemberSources.None(members)
            : PortiaHttpMemberSources.Resolve(typeof(TRequest), members, _queryMembers, _headerMembers,
                app.ServiceProvider.GetRequiredService<IOptions<PortiaHttpOptions>>().Value.HeaderBindings);
    }

    public void NoInput()
    {
        EnsureMutable();
        if (BodyType is not null || _parameters.Count != 0)
            throw new InvalidOperationException("NoInput cannot be combined with custom input declarations.");
        _noInput = true;
    }

    public void Produces(int status)
    {
        EnsureMutable();
        _responses.Add(new ProducesResponseTypeMetadata(status, null, []));
    }

    public void Created(string template, Type? payloadType)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        if (_hasResultHandler)
            throw new InvalidOperationException("Created cannot be combined with OnResult.");
        if (_created is not null)
            throw new InvalidOperationException("Created can only be configured once.");
        _created = (template, payloadType);
    }

    public void Produces<TBody>(int status, string contentType)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        _responses.Add(typeof(Stream).IsAssignableFrom(typeof(TBody))
            ? new PortiaStreamProducesMetadata(status, typeof(TBody), [contentType])
            : new ProducesResponseTypeMetadata(status, typeof(TBody), [contentType]));
    }

    public void RegisterResultHandler()
    {
        EnsureMutable();
        if (_created is not null)
            throw new InvalidOperationException("Created cannot be combined with OnResult.");
        _hasResultHandler = true;
    }

    public IResult? EnsureResultHandlerResponse(bool success, IResult? response)
    {
        if (success && response is null && _responses.Any(item => ResponseStatus(item) is >= 200 and < 400))
            throw new InvalidOperationException(
                "OnResult must return a response for a successful result when a custom success response is declared.");
        return response;
    }

    public void Validate(bool binderRequired, PortiaHttpMember[] members)
    {
        if ((_queryMembers.Count != 0 || _headerMembers.Count != 0) && Binder is not null)
            throw new InvalidOperationException(
                "FromQuery and FromHeader apply to default binding; declare OnBind inputs with Parameter.");
        if (_descriptions.Count > 0 && Binder is not null)
            throw new InvalidOperationException(
                "Describe applies to default binding; describe OnBind inputs with Parameter.");
        foreach (var name in _descriptions.Keys)
        {
            if (!members.Any(member => string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Describe member '{name}' is not a constructor-bound request member.");
        }

        if (binderRequired && Binder is null)
            throw new InvalidOperationException("This request shape requires OnBind.");
        if (Binder is null && (_noInput || BodyType is not null || _parameters.Count != 0))
            throw new InvalidOperationException("Custom input declarations require OnBind.");
        if (Binder is not null && !_noInput && BodyType is null && _parameters.Count == 0)
            throw new InvalidOperationException("OnBind must declare its inputs with Accepts, Parameter, or NoInput.");
        _frozen = true;
    }

    public void ApplyMetadata(IEndpointRouteBuilder app, IEndpointConventionBuilder builder,
        IReadOnlyDictionary<string, Func<TRequest, object?>>? requestMembers = null,
        IReadOnlyDictionary<string, Func<object?, object?>>? payloadMembers = null,
        bool createPayloadIsNullable = false)
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

        builder.Add(endpoint => endpoint.Metadata.Add(new PortiaOpenApiDescriptions(
            new Dictionary<string, string>(_descriptions, StringComparer.OrdinalIgnoreCase),
            new Dictionary<int, string>(_responseDescriptions), _successDescription)));
        builder.Add(endpoint => endpoint.Metadata.Add(new PortiaOpenApiEnrichment(
            [.. _requestExamples], [.. _responseExamples], [.. _responseHeaders])));
        foreach (var response in _responses)
            builder.Add(endpoint => endpoint.Metadata.Add(response));
        var endpointValidation = Binder is not null || _created is not null
            ? validations.BeginEndpointValidation()
            : null;
        if (_created is { } created)
            builder.Add(endpoint =>
            {
                var route = (endpoint as RouteEndpointBuilder)?.RoutePattern ??
                            throw new InvalidOperationException("Created requires route metadata.");
                endpoint.Metadata.Add(new PortiaCreatedLocation<TRequest>(created.Template, createPayloadIsNullable,
                    route, requestMembers ??
                    throw new InvalidOperationException("Created requires generated request metadata."), payloadMembers));
                var operation = endpoint.Metadata.OfType<PortiaOpenApiOperation>().Single();
                endpoint.Metadata[endpoint.Metadata.IndexOf(operation)] = operation with
                {
                    ResultType = created.PayloadType,
                    NoContent = created.PayloadType is null,
                    ResultIsNullable = createPayloadIsNullable
                };
                var contentTypes = created.PayloadType is null ? Array.Empty<string>() : ["application/json"];
                endpoint.Metadata.Add(new PortiaCreatedResponseMetadata(StatusCodes.Status200OK, created.PayloadType,
                    contentTypes));
                endpoint.Metadata.Add(new PortiaCreatedResponseMetadata(StatusCodes.Status201Created, created.PayloadType,
                    contentTypes));
            });
        if (_hasResultHandler)
            builder.Add(endpoint => endpoint.Metadata.Add(new PortiaCustomHttpResult(
                [.. _responses.Select(ResponseStatus)])));
        if (Binder is not null)
            builder.Add(endpoint =>
            {
                var route = (endpoint as RouteEndpointBuilder)?.RoutePattern ??
                            throw new InvalidOperationException("Portia HTTP endpoints require route metadata.");
                var routeNames = route.Parameters.Select(item => item.Name).ToArray();
                var declared = _parameters.Where(item => item.Location == PortiaHttpParameterLocation.Route)
                    .Select(item => item.Name).ToArray();
                if (routeNames.Length != declared.Length ||
                    routeNames.Any(name => !declared.Contains(name, StringComparer.Ordinal)))
                    throw new InvalidOperationException(
                        "Custom route parameters must exactly match the complete route pattern, including route groups and casing.");
                endpoint.Metadata.Add(new PortiaCustomHttpContract([.. _parameters]));
            });
        if (endpointValidation is not null)
            builder.Finally(_ => endpointValidation.Complete());
    }

    void SetBinder(Func<HttpContext, CancellationToken, ValueTask<TRequest>> binder)
    {
        EnsureMutable();
        if (Binder is not null)
            throw new InvalidOperationException("OnBind can only be configured once.");
        Binder = binder;
    }

    void EnsureInput()
    {
        if (_noInput)
            throw new InvalidOperationException("Custom input declarations cannot be combined with NoInput.");
    }

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

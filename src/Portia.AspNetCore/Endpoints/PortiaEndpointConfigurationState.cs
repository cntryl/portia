using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

sealed class PortiaEndpointConfigurationState<TRequest>
{
    readonly List<PortiaHttpContractParameter> _parameters = [];
    readonly List<string> _queryMembers = [];
    readonly List<object> _responses = [];
    bool _frozen;
    bool _hasResultHandler;
    bool _noInput;
    bool[] _queryMemberFlags = [];
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

    public void Parameter<T>(string name, PortiaHttpParameterLocation location, bool required)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureInput();
        if (_parameters.Any(item =>
                item.Location == location && StringComparer.OrdinalIgnoreCase.Equals(item.Name, name)))
            throw new InvalidOperationException(
                $"The custom {location} parameter '{name}' is declared more than once.");
        _parameters.Add(new PortiaHttpContractParameter(name, typeof(T), location, required));
    }

    public void FromQuery(string member)
    {
        EnsureMutable();
        if (_queryMembers.Contains(member, StringComparer.Ordinal))
            throw new InvalidOperationException($"FromQuery is declared more than once for '{member}'.");
        _queryMembers.Add(member);
    }

    public bool IsQueryMember(int index) => (uint)index < (uint)_queryMemberFlags.Length && _queryMemberFlags[index];

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
        if (_queryMembers.Count != 0 && Binder is not null)
            throw new InvalidOperationException(
                "FromQuery applies to default binding; declare OnBind inputs with Parameter.");
        var flags = new bool[members.Length];
        foreach (var name in _queryMembers)
        {
            var index = Array.FindIndex(members,
                item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new InvalidOperationException(
                    $"FromQuery member '{name}' is not a constructor-bound request member.");
            if (members[index].Source == "route")
                throw new InvalidOperationException($"FromQuery member '{name}' is already bound from the route.");
            if (!members[index].Scalar)
                throw new InvalidOperationException(
                    $"FromQuery member '{name}' must be a string, enum, or TryParse scalar.");
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

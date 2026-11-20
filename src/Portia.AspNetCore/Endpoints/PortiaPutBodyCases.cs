using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

/// <summary>Maps a PUT endpoint selected by a string member of its JSON body.</summary>
public static class PortiaPutBodyExtensions
{
    /// <summary>Maps a PUT whose typed cases all return 204 on success.</summary>
    public static PortiaPutBodyCases MapPortiaPutByBody(this IEndpointRouteBuilder app, string pattern,
        string operationId, string selector, params string[] queryMembers) =>
        new(app, pattern, operationId, selector, queryMembers);

    /// <summary>Maps a PUT whose typed cases all return the same JSON result on success.</summary>
    public static PortiaPutBodyCases<TOut> MapPortiaPutByBody<TOut>(this IEndpointRouteBuilder app, string pattern,
        string operationId, string selector, params string[] queryMembers) =>
        new(app, pattern, operationId, selector, queryMembers);
}

/// <summary>Declares cases for a PUT endpoint returning 204.</summary>
public sealed class PortiaPutBodyCases : PortiaPutBodyCasesBase
{
    internal PortiaPutBodyCases(IEndpointRouteBuilder app, string pattern, string operationId, string selector,
        string[] queryMembers) : base(app, pattern, operationId, selector, queryMembers, null)
    {
    }

    /// <summary>Associates a selector value with a no-result request.</summary>
    public PortiaPutBodyCases Case<TRequest>(string value) where TRequest : IRequest =>
        throw InterceptionRequired();
}

/// <summary>Declares cases for a PUT endpoint returning a common JSON result.</summary>
public sealed class PortiaPutBodyCases<TOut> : PortiaPutBodyCasesBase
{
    internal PortiaPutBodyCases(IEndpointRouteBuilder app, string pattern, string operationId, string selector,
        string[] queryMembers) : base(app, pattern, operationId, selector, queryMembers, typeof(TOut))
    {
    }

    /// <summary>Associates a selector value with a request producing the common result.</summary>
    public PortiaPutBodyCases<TOut> Case<TRequest>(string value) where TRequest : IRequest<TOut> =>
        throw InterceptionRequired();
}

/// <summary>Provides endpoint conventions and the generated registration surface for a typed PUT.</summary>
public abstract class PortiaPutBodyCasesBase : IEndpointConventionBuilder
{
    readonly IEndpointRouteBuilder _app;
    readonly IEndpointConventionBuilder _endpoint;
    readonly Dictionary<string, PutBodyCase> _cases = [with(StringComparer.Ordinal)];
    readonly string _selector;
    readonly string[] _queryMembers;
    bool _sealed;

    private protected PortiaPutBodyCasesBase(IEndpointRouteBuilder app, string pattern, string operationId,
        string selector, string[] queryMembers, Type? resultType)
    {
        PortiaHttpBinding.RequireHttpServices(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(queryMembers);
        _app = app;
        _selector = selector;
        _queryMembers = [.. queryMembers];
        RequestDelegate handler = DispatchAsync;
        _endpoint = app.MapMethods(pattern, [HttpMethods.Put], handler).WithMetadata(handler.Method)
            .WithName(operationId);
        _endpoint.Add(endpoint =>
        {
            if (_cases.Count == 0)
                throw new InvalidOperationException("MapPortiaPutByBody requires at least one typed case.");
            _sealed = true;
            var first = _cases.Values.First();
            endpoint.Metadata.Add(new PortiaPutBodyOperation(_selector, [.. _cases.Values]));
            endpoint.Metadata.Add(new PortiaOpenApiOperation(operationId, resultType, resultType is null,
                false, false, false, first.Parameters)
            {
                RequestType = first.RequestType,
                ResultIsNullable = first.ResultIsNullable
            });
        });
    }

    private protected InvalidOperationException InterceptionRequired() =>
        new($"Chain Case directly from MapPortiaPutByBody to generate binding for selector '{_selector}'.");

    /// <inheritdoc />
    public void Add(Action<EndpointBuilder> convention) => _endpoint.Add(convention);

    /// <inheritdoc />
    public void Finally(Action<EndpointBuilder> finallyConvention) => _endpoint.Finally(finallyConvention);

    /// <summary>Resolves generated path, query and header binding for one case.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaHttpMemberSources ResolveSources<TRequest>(PortiaHttpMember[] members, bool hasConditional)
    {
        var sources = PortiaHttpMemberSources.Resolve(typeof(TRequest), members, _queryMembers,
            new Dictionary<string, string>(), _app.ServiceProvider.GetRequiredService<IOptions<PortiaHttpOptions>>()
                .Value.HeaderBindings);
        if (hasConditional)
            PortiaHttpBinding.ValidateRequiredWhen<TRequest>(_app, members, sources, hasCustomBinder: false);
        return sources;
    }

    /// <summary>Registers the generated binder and input contract for one case.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AddGeneratedCase<TRequest>(string value, PortiaOpenApiParameter[] parameters,
        PortiaHttpMemberSources sources, bool resultIsNullable,
        Func<HttpContext, IRequestBus, JsonElement, CancellationToken, Task<IResult>> handle)
    {
        if (_sealed)
            throw new InvalidOperationException("Register typed cases before building the endpoint.");
        ArgumentNullException.ThrowIfNull(value);
        var type = typeof(TRequest);
        var options = _app.ServiceProvider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
        var registration = _app.ServiceProvider.GetRequiredService<RequestRegistry>().Handler(type);
        if (registration.Permission is not null && registration.StaticPermission is null)
            throw new InvalidOperationException("Typed PUT cases require static permission declarations. Use an authorizer for contextual checks.");

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var name = parameter.WireName ?? options.PropertyNamingPolicy?.ConvertName(parameter.ClrName) ??
                       parameter.ClrName;
            var comparison = SelectorComparison(options);
            if (parameter.Source == "body")
            {
                var binding = PortiaHttpBinding.ResolveBodyMember(options, type, parameter.Type, i,
                    parameter.ClrName, name);
                if (string.Equals(binding.Name, _selector, comparison) &&
                    (binding.Name != _selector || parameter.Type != typeof(string) || !binding.Input ||
                     sources.Query(i) || sources.Header(i) is not null ||
                     !ReferenceEquals(binding.Options.GetTypeInfo<string>().Converter, JsonMetadataServices.StringConverter)))
                    throw new InvalidOperationException($"Typed PUT selector '{_selector}' needs ordinary string JSON binding.");
            }
            else if (string.Equals(name, _selector, comparison))
            {
                throw new InvalidOperationException($"Typed PUT selector '{_selector}' must bind only from JSON.");
            }

            if (sources.Header(i) is { } header)
                parameter = parameter with { Source = "header", WireName = header };
            else if (sources.Query(i))
                parameter = parameter with { Source = "query" };

            parameters[i] = parameter;
        }

        if (_cases.Values.FirstOrDefault() is { } first)
        {
            var common = TransportMembers(first.Parameters).ToArray();
            var incoming = TransportMembers(parameters).ToArray();
            if (first.ResultIsNullable != resultIsNullable || !common.SequenceEqual(incoming) ||
                common.Any(parameter => !JsonNode.DeepEquals(Facts(first.RequestType, parameter), Facts(type, parameter))))
                throw new InvalidOperationException("Typed PUT cases must share the same path, query, header and response contract.");
        }
        if (!_cases.TryAdd(value, new PutBodyCase(value, type, parameters, registration.StaticPermission,
                resultIsNullable, handle)))
            throw new InvalidOperationException($"Typed PUT selector value '{value}' was registered twice.");

        JsonNode? Facts(Type request, PortiaOpenApiParameter parameter)
        {
            var member = PortiaJsonDescriptions.MemberMetadata(options, request, parameter.ClrName);
            if (member is null || member.Rules.Count == 0)
                return null;
            var schema = new PortiaOpenApiJsonSchemaExporter(options, null, null, null, true)
                .Export(parameter.Type, parameter.IsNullable);
            if (schema is JsonObject obj)
                PortiaJsonConstraints.Apply(obj, member, options);
            return schema;
        }

        static IEnumerable<PortiaOpenApiParameter> TransportMembers(PortiaOpenApiParameter[] parameters) =>
            parameters.Where(parameter => parameter.Source != "body")
                .OrderBy(parameter => parameter.ClrName, StringComparer.Ordinal);
    }

    async Task DispatchAsync(HttpContext http)
    {
        try
        {
            if (PortiaHttpBinding.RejectCrossOrigin(http) is { } rejection)
            {
                await rejection.ExecuteAsync(http).ConfigureAwait(false);
                return;
            }

            var ct = http.RequestAborted;
            var options = PortiaHttpBinding.GetJsonOptions(http);
            using var body = await ReadBodyAsync(http, ct).ConfigureAwait(false);
            var value = ReadSelector(body.RootElement, SelectorComparison(options));
            if (!_cases.TryGetValue(value, out var selected))
                throw new BadHttpRequestException($"Unknown value for typed PUT selector '{_selector}'.");
            var bus = http.RequestServices.GetRequiredService<IRequestBus>();
            var result = await selected.Handle(http, bus, body.RootElement, ct).ConfigureAwait(false);
            await result.ExecuteAsync(http).ConfigureAwait(false);
        }
        catch (HttpPayloadTooLargeException exception) when (!http.Response.HasStarted)
        {
            await PortiaHttpBinding.Problem(413, exception.Message).ExecuteAsync(http).ConfigureAwait(false);
        }
        catch (HttpUnsupportedMediaTypeException exception) when (!http.Response.HasStarted)
        {
            await PortiaHttpBinding.Problem(415, exception.Message).ExecuteAsync(http).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception) when (!http.Response.HasStarted)
        {
            await PortiaHttpBinding.Problem(400, exception.Message).ExecuteAsync(http).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            http.Abort();
        }
        catch (Exception exception) when (!http.Response.HasStarted)
        {
            await PortiaHttpBinding.Unexpected(http, exception).ExecuteAsync(http).ConfigureAwait(false);
        }
        catch (Exception)
        {
            http.Abort();
        }
    }

    static async ValueTask<JsonDocument> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        try
        {
            return await PortiaHttpBinding.ReadJsonBodyAsync(http, true, ct).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new BadHttpRequestException("Malformed request.", exception);
        }
    }

    string ReadSelector(JsonElement body, StringComparison comparison)
    {
        string? value = null;
        if (body.ValueKind != JsonValueKind.Object)
            throw new BadHttpRequestException("Expected a JSON object.");
        foreach (var property in body.EnumerateObject())
        {
            if (!string.Equals(property.Name, _selector, comparison))
                continue;
            if (value is not null || property.Value.ValueKind != JsonValueKind.String)
                throw new BadHttpRequestException($"Typed PUT selector '{_selector}' must be one string value.");
            value = property.Value.GetString();
        }

        return value ?? throw new BadHttpRequestException($"Missing typed PUT selector '{_selector}'.");
    }

    static StringComparison SelectorComparison(JsonSerializerOptions options) => options.PropertyNameCaseInsensitive
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

sealed record PortiaPutBodyOperation(string Selector, PutBodyCase[] Cases);

sealed record PutBodyCase(string Value, Type RequestType, PortiaOpenApiParameter[] Parameters, string? Permission,
    bool ResultIsNullable, Func<HttpContext, IRequestBus, JsonElement, CancellationToken, Task<IResult>> Handle);

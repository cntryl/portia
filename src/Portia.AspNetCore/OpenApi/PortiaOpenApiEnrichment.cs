using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

sealed record PortiaHttpExample(PortiaContractExample Example, string ContentType, int? Status);

sealed record PortiaHttpResponseHeader(int Status, string Name, Type Type, string Description, bool Required);

sealed record PortiaOpenApiEnrichment(
    IReadOnlyList<PortiaHttpExample> Requests,
    IReadOnlyList<PortiaHttpExample> Responses,
    IReadOnlyList<PortiaHttpResponseHeader> Headers)
{
    internal void Apply(
        OpenApiOperation operation,
        PortiaOpenApiOperation metadata,
        IList<object> endpoint,
        IServiceProvider services,
        JsonSerializerOptions json,
        OpenApiDocument document,
        PortiaHttpOptions options
    )
    {
        var contracts = services.GetRequiredService<PortiaContractMetadataRegistry>();
        var copied = new HashSet<string>(StringComparer.Ordinal);

        OpenApiResponse Response(int status)
        {
            var key = status.ToString(CultureInfo.InvariantCulture);

            if (operation.Responses?.TryGetValue(key, out var response) != true || response is null)
                throw new InvalidOperationException($"HTTP enrichment refers to undeclared response status {status}.");

            if (copied.Add(key))
            {
                var clone = new OpenApiResponse
                {
                    Description = response.Description,
                    Links = response.Links,
                    Extensions = response.Extensions,
                    Headers =
                        response.Headers is null ? null : new Dictionary<string, IOpenApiHeader>(response.Headers),
                    Content = response.Content?.ToDictionary(item => item.Key,
                        item => (IOpenApiMediaType)new OpenApiMediaType
                        {
                            Schema = item.Value.Schema,
                            Example = item.Value.Example,
                            Examples = item.Value.Examples,
                            Encoding = item.Value.Encoding
                        }, StringComparer.Ordinal)
                };
                operation.Responses[key] = clone;
                return clone;
            }

            return (OpenApiResponse)response;
        }

        foreach (var header in Headers)
        {
            var response = Response(header.Status);
            response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.OrdinalIgnoreCase);
            response.Headers[header.Name] = new OpenApiHeader
            {
                Description = header.Description,
                Required = header.Required,
                Schema = PortiaOpenApiSchemaGenerator.Create(header.Type, json, document, options.OpenApiSchemaType,
                    schemaCollectionElementIsNullable: options.OpenApiSchemaCollectionElementIsNullable,
                    descriptions: options.OpenApi)
            };
        }

        var responseTypes = endpoint.OfType<IProducesResponseTypeMetadata>()
            .Select(item => (item.StatusCode, Type: ResponseType(item)))
            .Where(item => item.Type is not null)
            .GroupBy(item => item.StatusCode).ToDictionary(group => group.Key, group => group.Last().Type!);

        if (metadata.ResultType is { } resultType && !metadata.JsonStream && !metadata.ServerSentEvents &&
            !metadata.NoContent && !endpoint.OfType<PortiaCreatedResponseMetadata>().Any())
            _ = responseTypes.TryAdd(200, resultType);

        foreach (var (status, type) in responseTypes)
        {
            var key = status.ToString(CultureInfo.InvariantCulture);

            if (operation.Responses?.TryGetValue(key, out var response) != true || response?.Content is null)
                continue;

            var examples = contracts.Examples.Resolve(json, type, input: false);

            foreach (var contentType in response.Content.Keys.Where(name =>
                         name == "application/json" || name.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
                foreach (var example in examples)
                    Add(Response(status).Content![contentType], example);
        }

        foreach (var example in Responses)
        {
            var declaredMetadata = endpoint.OfType<IProducesResponseTypeMetadata>()
                .LastOrDefault(item => item.StatusCode == example.Status);
            var declared = declaredMetadata is not null ? ResponseType(declaredMetadata) :
                example.Status == 200 && !metadata.NoContent && !metadata.JsonStream &&
                !metadata.ServerSentEvents && !endpoint.OfType<PortiaCreatedResponseMetadata>().Any()
                    ? metadata.ResultType
                    : null;

            if (declared != example.Example.Type)
                throw new InvalidOperationException(
                    $"Response example '{example.Example.Name}' must use the declared response type '{declared}'.");

            var response = Response(example.Status!.Value);

            if (response.Content?.TryGetValue(example.ContentType, out var media) != true || media is null)
                throw new InvalidOperationException(
                    $"Response example '{example.Example.Name}' refers to undeclared content type '{example.ContentType}'.");

            Add(media, PortiaContractExamples.Resolve(json, contracts, example.Example, input: false));
        }

        var custom = endpoint.OfType<PortiaCustomHttpContract>().Any();
        var requestType =
            custom ? endpoint.OfType<IAcceptsMetadata>().LastOrDefault()?.RequestType : metadata.RequestType;
        var input = requestType is null ? [] : contracts.Examples.Resolve(json, requestType, input: true);

        foreach (var example in input)
            AddRequest(example, "application/json");

        foreach (var example in Requests)
        {
            if (requestType != example.Example.Type)
                throw new InvalidOperationException(
                    $"Request example '{example.Example.Name}' must use the declared request type '{requestType}'.");

            AddRequest(PortiaContractExamples.Resolve(json, contracts, example.Example, input: true),
                example.ContentType);
        }

        void AddRequest(PortiaResolvedExample example, string contentType)
        {
            var value = example.Value;

            if (!custom)
            {
                if (value is not JsonObject obj)
                    throw new InvalidOperationException(
                        $"Request example '{example.Name}' requires an object for generated member binding.");

                var body = new JsonObject();
                var queries = endpoint.OfType<PortiaQueryMembers>().SingleOrDefault()?.Names ?? [];
                var headers = endpoint.OfType<PortiaHeaderMembers>().SingleOrDefault()?.Members ?? [];

                for (var index = 0; index < metadata.Parameters.Count; index++)
                {
                    var parameter = metadata.Parameters[index];
                    var header = headers.SingleOrDefault(item =>
                        string.Equals(item.MemberName, parameter.ClrName, StringComparison.OrdinalIgnoreCase));
                    var fallback = parameter.WireName ??
                                   json.PropertyNamingPolicy?.ConvertName(parameter.ClrName) ?? parameter.ClrName;
                    var member = PortiaHttpBinding.ResolveBodyMember(json, requestType!, parameter.Type, index,
                        parameter.ClrName, fallback);

                    if (parameter.Source == "body" &&
                        !queries.Contains(parameter.ClrName, StringComparer.OrdinalIgnoreCase) && header is null)
                    {
                        if (member.Input && obj.TryGetPropertyValue(member.Name, out var item))
                            body[member.Name] = item?.DeepClone();
                    }
                    else if (obj.TryGetPropertyValue(member.Name, out var item))
                    {
                        var target = operation.Parameters?.OfType<OpenApiParameter>()
                            .SingleOrDefault(item => item.Name == (header?.Name ?? fallback));

                        if (target is not null)
                        {
                            target.Examples ??= new Dictionary<string, IOpenApiExample>(StringComparer.Ordinal);
                            target.Example = null;
                            target.Examples[example.Name] = new OpenApiExample
                            {
                                Summary = example.Summary,
                                Value = example.Members?.TryGetValue(member.Name, out var scalar) == true
                                    ? Scalar(scalar, item, target.Schema)
                                    : item?.DeepClone()
                            };
                        }
                    }
                }


                value = body;

                if (operation.RequestBody is null && body.Count == 0)
                    return;
            }



            if (operation.RequestBody?.Content?.TryGetValue(contentType, out var media) != true || media is null)
                throw new InvalidOperationException(
                    $"Request example '{example.Name}' refers to undeclared content type '{contentType}'.");

            Add(media, example with { Value = value });
        }
    }

    static Type? ResponseType(IProducesResponseTypeMetadata metadata) =>
        metadata is PortiaCreatedResponseMetadata created ? created.PayloadType : metadata.Type;

    internal static JsonNode? Scalar(object? value, JsonNode? fallback, IOpenApiSchema? schema) => value switch
    {
        null => null,
        string item => JsonValue.Create(item),
        bool item => JsonValue.Create(item),
        byte item => JsonValue.Create(item),
        sbyte item => JsonValue.Create(item),
        short item => JsonValue.Create(item),
        ushort item => JsonValue.Create(item),
        int item => JsonValue.Create(item),
        uint item => JsonValue.Create(item),
        long item => JsonValue.Create(item),
        ulong item => JsonValue.Create(item),
        float item => JsonValue.Create(item),
        double item => JsonValue.Create(item),
        decimal item => JsonValue.Create(item),
        char item => JsonValue.Create(item.ToString()),
        Guid item => JsonValue.Create(item.ToString("D", CultureInfo.InvariantCulture)),
        DateTime item => JsonValue.Create(item.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset item => JsonValue.Create(item.ToString("O", CultureInfo.InvariantCulture)),
        DateOnly item => JsonValue.Create(item.ToString("O", CultureInfo.InvariantCulture)),
        TimeOnly item => JsonValue.Create(item.ToString("O", CultureInfo.InvariantCulture)),
        TimeSpan item => JsonValue.Create(item.ToString("c", CultureInfo.InvariantCulture)),
        Enum item => EnumScalar(item, fallback, schema),
        IFormattable item => JsonValue.Create(item.ToString(null, CultureInfo.InvariantCulture)),
        _ => fallback?.DeepClone()
    };

    static JsonNode? EnumScalar(Enum value, JsonNode? fallback, IOpenApiSchema? schema)
    {
        if (schema?.Type is { } type && (type & (JsonSchemaType.Integer | JsonSchemaType.Number)) != 0)
            return JsonNode.Parse(value.ToString("D"));

        var name = value.ToString();

        if (schema?.Enum is { Count: > 0 } names)
        {
            var match = names.OfType<JsonValue>().FirstOrDefault(item => item.TryGetValue<string>(out var text) &&
                                                                         string.Equals(text, name,
                                                                             StringComparison.OrdinalIgnoreCase));
            return match?.DeepClone() ?? throw new InvalidOperationException(
                $"HTTP enum parameter example '{name}' has no schema name accepted by scalar binding. Configure a compatible HTTP schema or an explicit OnBind contract.");
        }



        if (fallback is JsonValue encoded && encoded.TryGetValue<string>(out var encodedName) &&
            string.Equals(encodedName, name, StringComparison.OrdinalIgnoreCase))
            return fallback.DeepClone();

        return JsonValue.Create(name);
    }

    static void Add(IOpenApiMediaType media, PortiaResolvedExample example)
    {
        if (media is not OpenApiMediaType target)
            throw new InvalidOperationException("Examples require an inline media type.");

        target.Examples = target.Examples is null
            ? [with(StringComparer.Ordinal)]
            : new Dictionary<string, IOpenApiExample>(target.Examples, StringComparer.Ordinal);
        target.Example = null;
        target.Examples[example.Name] = new OpenApiExample
        { Summary = example.Summary, Value = example.Value?.DeepClone() };
    }
}

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

sealed class PortiaOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    static readonly ConditionalWeakTable<OpenApiDocument, DefaultErrorResponses> DefaultErrorResponsesByDocument = [];

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata?.OfType<PortiaOpenApiOperation>()
            .SingleOrDefault();
        if (metadata == null)
            return Task.CompletedTask;

        cancellationToken.ThrowIfCancellationRequested();
        operation.Responses ??= [];
        var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata ?? [];
        var explicitResponses = endpointMetadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(item => new DeclaredResponse(item.StatusCode, item.Type, item.ContentTypes.ToArray(), false))
            .Concat(endpointMetadata.OfType<PortiaStreamProducesMetadata>()
                .Select(item => new DeclaredResponse(item.StatusCode, item.Type, item.ContentTypes, true)))
            .ToArray();
        var explicitStatuses = explicitResponses
            .Select(item => item.StatusCode.ToString(CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);
        var customResult = endpointMetadata
            .OfType<PortiaCustomHttpResult>().SingleOrDefault();
        var customStatuses = customResult?.DeclaredStatusCodes
            .Select(status => status.ToString(CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var replacesDefaultSuccess =
            customResult?.DeclaredStatusCodes.Any(status => status is >= 200 and < 400) == true;
        foreach (var status in operation.Responses.Keys.Where(status => !explicitStatuses.Contains(status)).ToArray())
            _ = operation.Responses.Remove(status);
        if (replacesDefaultSuccess)
        {
            if (!customStatuses.Contains("200"))
                _ = operation.Responses.Remove("200");
            if (!customStatuses.Contains("204"))
                _ = operation.Responses.Remove("204");
        }

        void SetDefaultResponse(string status, IOpenApiResponse response)
        {
            if (!explicitStatuses.Contains(status))
                operation.Responses[status] = response;
        }

        operation.OperationId = metadata.OperationId;
        operation.Parameters = [];
        var httpOptions = context.ApplicationServices.GetRequiredService<IOptions<PortiaHttpOptions>>().Value;
        var schemaType = httpOptions.OpenApiSchemaType;
        var schemaCollectionElementIsNullable = httpOptions.OpenApiSchemaCollectionElementIsNullable;
        var jsonOptions = context.ApplicationServices.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
        foreach (var responseMetadata in explicitResponses)
        {
            var status = responseMetadata.StatusCode.ToString(CultureInfo.InvariantCulture);
            if (replacesDefaultSuccess && (status == "200" || status == "204") &&
                !customStatuses.Contains(status))
                continue;
            if (!responseMetadata.ReplaceExisting && operation.Responses.ContainsKey(status))
                continue;
            var response = new OpenApiResponse { Description = "Response" };
            if (responseMetadata.Type is not null && responseMetadata.ContentTypes.Any())
            {
                response.Content = responseMetadata.ContentTypes.Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(contentType => contentType,
                        _ => new OpenApiMediaType
                        {
                            Schema = PortiaOpenApiSchemaGenerator.Create(responseMetadata.Type, jsonOptions,
                                context.Document!, schemaType,
                                schemaCollectionElementIsNullable: schemaCollectionElementIsNullable)
                        }, StringComparer.Ordinal);
            }

            operation.Responses[status] = response;
        }

        var custom = endpointMetadata.OfType<PortiaCustomHttpContract>()
            .SingleOrDefault();
        foreach (var parameter in custom?.Parameters ?? [])
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = parameter.Name,
                In = parameter.Location switch
                {
                    PortiaHttpParameterLocation.Route => ParameterLocation.Path,
                    PortiaHttpParameterLocation.Query => ParameterLocation.Query,
                    PortiaHttpParameterLocation.Header => ParameterLocation.Header,
                    PortiaHttpParameterLocation.Cookie => ParameterLocation.Cookie,
                    _ => throw new InvalidOperationException("Unknown Portia HTTP parameter location.")
                },
                Required = parameter.Location == PortiaHttpParameterLocation.Route || parameter.Required,
                Schema = PortiaOpenApiSchemaGenerator.Create(parameter.Type, jsonOptions, context.Document!, schemaType,
                    schemaCollectionElementIsNullable: schemaCollectionElementIsNullable)
            });
        }

        var queryMembers = endpointMetadata.OfType<PortiaQueryMembers>()
            .SingleOrDefault()?.Names ?? [];

        bool IsBody(PortiaOpenApiParameter parameter)
        {
            return parameter.Source == "body" &&
                   !queryMembers.Contains(parameter.ClrName, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var parameter in custom is null ? metadata.Parameters.Where(parameter => !IsBody(parameter)) : [])
        {
            var schema = PortiaOpenApiSchemaGenerator.Create(parameter.Type, jsonOptions, context.Document!, schemaType,
                parameter.IsNullable, schemaCollectionElementIsNullable);
            if (parameter.HasDefault && parameter.DefaultValue is not null && schema is OpenApiSchema concreteSchema)
            {
                concreteSchema.Default =
                    JsonSerializer.SerializeToNode(parameter.DefaultValue, jsonOptions.GetTypeInfo(parameter.Type));
            }

            operation.Parameters.Add(new OpenApiParameter
            {
                Name = parameter.WireName ?? jsonOptions.PropertyNamingPolicy?.ConvertName(parameter.ClrName) ??
                    parameter.ClrName,
                In = parameter.Source == "route" ? ParameterLocation.Path : ParameterLocation.Query,
                Required = parameter.Source == "route" || parameter.Required,
                Schema = schema
            });
        }

        var accepts = endpointMetadata.OfType<PortiaStreamAcceptsMetadata>()
            .Select(item => new DeclaredAccepts(item.RequestType, item.IsOptional, item.ContentTypes))
            .SingleOrDefault() ?? endpointMetadata.OfType<IAcceptsMetadata>()
            .Select(item => new DeclaredAccepts(item.RequestType, item.IsOptional, item.ContentTypes.ToArray()))
            .LastOrDefault();
        var body = custom is null ? metadata.Parameters.Where(IsBody).ToArray() : [];
        if (custom is not null && accepts?.RequestType is not null)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = !accepts.IsOptional,
                Content = accepts.ContentTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(
                    contentType => contentType,
                    _ => new OpenApiMediaType
                    {
                        Schema = PortiaOpenApiSchemaGenerator.Create(accepts.RequestType, jsonOptions,
                            context.Document!, schemaType,
                            schemaCollectionElementIsNullable: schemaCollectionElementIsNullable)
                    }, StringComparer.Ordinal)
            };
        }

        if (body.Length > 0)
        {
            var properties = new Dictionary<string, IOpenApiSchema>();
            var jsonRequired = new HashSet<string>();
            var formRequired = new HashSet<string>();
            foreach (var parameter in body)
            {
                var name = parameter.WireName ?? jsonOptions.PropertyNamingPolicy?.ConvertName(parameter.ClrName) ??
                    parameter.ClrName;
                properties[name] = PortiaOpenApiSchemaGenerator.Create(parameter.Type, jsonOptions, context.Document!,
                    schemaType, parameter.IsNullable, schemaCollectionElementIsNullable);
                if (parameter.Required)
                {
                    _ = jsonRequired.Add(name);
                    if (parameter.Type != typeof(bool))
                        _ = formRequired.Add(name);
                }
            }

            var schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = properties,
                Required = jsonRequired.Count == 0 ? null : jsonRequired
            };
            var content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } };
            if (endpointMetadata.OfType<PortiaFormBindableBody>().Any() &&
                PortiaHttpBinding.AcceptsFormBody(endpointMetadata, context.ApplicationServices))
            {
                var formSchema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = properties,
                    Required = formRequired.Count == 0 ? null : formRequired
                };
                content["application/x-www-form-urlencoded"] = new OpenApiMediaType { Schema = formSchema };
                content["multipart/form-data"] = new OpenApiMediaType { Schema = formSchema };
            }

            operation.RequestBody = new OpenApiRequestBody
            {
                Required = body.Any(parameter => parameter.Required),
                Content = content
            };
        }

        if (replacesDefaultSuccess)
        {
            // The configured result hook owns the declared success response. Portia's normal
            // failure responses remain available, but its JSON/no-content success does not.
        }
        else if (metadata.JsonStream || metadata.ServerSentEvents)
        {
            var item = PortiaOpenApiSchemaGenerator.Create(metadata.ResultType!, jsonOptions, context.Document!, schemaType,
                metadata.ResultIsNullable, schemaCollectionElementIsNullable);
            var schema = metadata.JsonStream
                ? new OpenApiSchema { Type = JsonSchemaType.Array, Items = item }
                : item;
            SetDefaultResponse("200",
                Response("OK", metadata.ServerSentEvents ? "text/event-stream" : "application/json", schema));
        }
        else if (metadata.NoContent)
        {
            SetDefaultResponse("204", new OpenApiResponse { Description = "No Content" });
        }
        else
        {
            var schema = PortiaOpenApiSchemaGenerator.Create(metadata.ResultType!, jsonOptions, context.Document!, schemaType,
                metadata.ResultIsNullable, schemaCollectionElementIsNullable);
            SetDefaultResponse("200", Response("OK", "application/json", schema));
        }

        if (metadata.QueueCapable && customResult is null)
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Prefer",
                In = ParameterLocation.Header,
                Required = false,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
            SetDefaultResponse("202", Response("Accepted", "application/json", new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    [jsonOptions.PropertyNamingPolicy?.ConvertName("RequestId") ?? "RequestId"] = new OpenApiSchema
                    { Type = JsonSchemaType.String, Format = "uuid" }
                },
                Required = new HashSet<string>
                    { jsonOptions.PropertyNamingPolicy?.ConvertName("RequestId") ?? "RequestId" }
            }));
        }

        var defaultErrorResponses = DefaultErrorResponsesByDocument.GetValue(context.Document!,
            static document => new DefaultErrorResponses(document));
        foreach (var status in new[] { "400", "401", "403", "404", "409", "413", "415", "500" })
        {
            if (explicitStatuses.Contains(status))
                continue;

            SetDefaultResponse(status,
                status == "401" ? defaultErrorResponses.Unauthorized : defaultErrorResponses.Problem);
        }

        return Task.CompletedTask;
    }

    static OpenApiResponse Response(string description, string mediaType, IOpenApiSchema schema) => new()
    {
        Description = description,
        Content = new Dictionary<string, OpenApiMediaType> { [mediaType] = new() { Schema = schema } }
    };

    sealed class DefaultErrorResponses(OpenApiDocument document)
    {
        const string ProblemName = "CntrylPortiaHttpProblemResponse";
        const string UnauthorizedName = "CntrylPortiaHttpUnauthorizedResponse";

        readonly object _gate = new();
        string? _problemName;
        string? _unauthorizedName;

        public IOpenApiResponse Problem => GetReference(ref _problemName, ProblemName, CreateProblemResponse);

        public IOpenApiResponse Unauthorized =>
            GetReference(ref _unauthorizedName, UnauthorizedName, CreateUnauthorizedResponse);

        OpenApiResponseReference GetReference(ref string? cachedName, string preferredName,
            Func<OpenApiResponse> createResponse)
        {
            lock (_gate)
            {
                if (cachedName is null)
                {
                    var components = document.Components ??= new OpenApiComponents();
                    var responses = components.Responses ??= new Dictionary<string, IOpenApiResponse>(StringComparer.Ordinal);
                    var name = preferredName;
                    for (var suffix = 2; responses.ContainsKey(name); suffix++)
                        name = $"{preferredName}_{suffix.ToString(CultureInfo.InvariantCulture)}";
                    responses.Add(name, createResponse());
                    cachedName = name;
                }

                return new OpenApiResponseReference(cachedName, document);
            }
        }

        static OpenApiResponse CreateProblemResponse()
        {
            var response = Response("Error", "application/problem+json", new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri-reference" },
                    ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                    ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["instance"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri-reference" },
                    ["transient"] = new OpenApiSchema { Type = JsonSchemaType.Boolean }
                },
                Required = new HashSet<string> { "type", "title", "status", "detail", "instance" }
            });
            response.Headers = new Dictionary<string, IOpenApiHeader>
            {
                [ResultHttpExtensions.TransientHeaderName] = new OpenApiHeader
                {
                    Required = false,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean }
                }
            };
            return response;
        }

        static OpenApiResponse CreateUnauthorizedResponse() => new()
        {
            Description = "Unauthorized",
            Headers = new Dictionary<string, IOpenApiHeader>
            {
                ["WWW-Authenticate"] = new OpenApiHeader
                {
                    Required = true,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                },
                [ResultHttpExtensions.TransientHeaderName] = new OpenApiHeader
                {
                    Required = true,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean }
                }
            }
        };
    }

    sealed record DeclaredAccepts(Type? RequestType, bool IsOptional, IReadOnlyList<string> ContentTypes);

    sealed record DeclaredResponse(
        int StatusCode,
        Type? Type,
        IReadOnlyList<string> ContentTypes,
        bool ReplaceExisting);
}

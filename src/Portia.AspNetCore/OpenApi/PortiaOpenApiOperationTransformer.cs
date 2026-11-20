using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

sealed class PortiaOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    static readonly ConditionalWeakTable<OpenApiDocument, DefaultErrorResponses> DefaultErrorResponsesByDocument = [];

    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata?.OfType<PortiaOpenApiOperation>()
            .SingleOrDefault();
        if (metadata == null)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        operation.Responses ??= [];
        var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata ?? [];
        var createdLocation = endpointMetadata.OfType<PortiaCreatedLocation>().SingleOrDefault();
        var explicitResponses = endpointMetadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(item => new DeclaredResponse(item.StatusCode,
                item is PortiaCreatedResponseMetadata created ? created.PayloadType : item.Type,
                item.ContentTypes.ToArray(), false))
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

        operation.OperationId = endpointMetadata.OfType<IEndpointNameMetadata>().LastOrDefault()?.EndpointName ??
                                metadata.OperationId;
        operation.Parameters = [];
        var httpOptions = context.ApplicationServices.GetRequiredService<IOptions<PortiaHttpOptions>>().Value;
        var descriptions = httpOptions.OpenApi;
        var endpointDescriptions = endpointMetadata.OfType<PortiaOpenApiDescriptions>().SingleOrDefault();
        var jsonOptions =
            context.ApplicationServices.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
        if (metadata.RequestType is { } operationRequest)
        {
            operation.Description ??= PortiaJsonDescriptions.Type(jsonOptions, operationRequest);
            operation.Deprecated |= PortiaJsonDescriptions.Metadata(jsonOptions, operationRequest)?.Deprecated == true;
        }

        string? MemberDescription(PortiaOpenApiParameter parameter, string wireName) =>
            endpointDescriptions?.Members.GetValueOrDefault(parameter.ClrName) ?? parameter.Description ??
            descriptions.Member(metadata.RequestType, parameter.ClrName, wireName, jsonOptions);

        var schemaType = httpOptions.OpenApiSchemaType;
        var schemaCollectionElementIsNullable = httpOptions.OpenApiSchemaCollectionElementIsNullable;
        foreach (var responseMetadata in explicitResponses)
        {
            var status = responseMetadata.StatusCode.ToString(CultureInfo.InvariantCulture);
            if (replacesDefaultSuccess && (status == "200" || status == "204") &&
                !customStatuses.Contains(status))
                continue;
            _ = operation.Responses.TryGetValue(status, out var existing);
            var response = new OpenApiResponse
            {
                Description = existing?.Description ?? "Response",
                Headers = existing?.Headers,
                Links = existing?.Links,
                Extensions = existing?.Extensions,
                Content = !responseMetadata.ReplaceExisting && existing?.Content is { } content
                    ? new Dictionary<string, IOpenApiMediaType>(content, StringComparer.Ordinal)
                    : null
            };
            var rootIsNullable = status is "200" or "201" && createdLocation?.PayloadIsNullable == true;
            if (responseMetadata.Type is { } responseType && responseType != typeof(void) &&
                responseMetadata.ContentTypes.Count > 0)
            {
                response.Content ??= new Dictionary<string, IOpenApiMediaType>(StringComparer.Ordinal);
                foreach (var contentType in responseMetadata.ContentTypes.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _ = response.Content.TryGetValue(contentType, out var media);
                    response.Content[contentType] = new OpenApiMediaType
                    {
                        Schema = PortiaOpenApiSchemaGenerator.Create(responseType, jsonOptions,
                            context.Document!, schemaType,
                            rootIsNullable: rootIsNullable,
                            schemaCollectionElementIsNullable: schemaCollectionElementIsNullable,
                            descriptions: descriptions),
                        Example = media?.Example,
                        Examples = media?.Examples,
                        Encoding = media?.Encoding,
                        Extensions = media?.Extensions
                    };
                }
            }

            operation.Responses[status] = response;
        }

        if (createdLocation is not null)
            foreach (var status in new[] { "200", "201", "204" })
                if (operation.Responses.TryGetValue(status, out var createResponse) && createResponse is OpenApiResponse concrete)
                {
                    concrete.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.OrdinalIgnoreCase);
                    concrete.Headers["Location"] = new OpenApiHeader
                    {
                        Description = "URI of the resource.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri-reference" }
                    };
                }

        var custom = endpointMetadata.OfType<PortiaCustomHttpContract>()
            .SingleOrDefault();
        foreach (var parameter in custom?.Parameters ?? [])
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = parameter.Name,
                Description = parameter.Description ?? descriptions.SharedMember(parameter.Name),
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
                    schemaCollectionElementIsNullable: schemaCollectionElementIsNullable, descriptions: descriptions,
                    input: true)
            });
        }

        var queryMembers = endpointMetadata.OfType<PortiaQueryMembers>()
            .SingleOrDefault()?.Names ?? [];
        var headerMembers = endpointMetadata.OfType<PortiaHeaderMembers>()
            .SingleOrDefault()?.Members ?? [];
        string? HeaderName(string memberName) => headerMembers.FirstOrDefault(item =>
            string.Equals(item.MemberName, memberName, StringComparison.OrdinalIgnoreCase))?.Name;

        bool IsBody(PortiaOpenApiParameter parameter)
        {
            return parameter.Source == "body" &&
                   !queryMembers.Contains(parameter.ClrName, StringComparer.OrdinalIgnoreCase) &&
                   HeaderName(parameter.ClrName) is null;
        }

        // Route, query and header members share one description so their schema, default and
        // required-ness cannot drift apart.
        foreach (var parameter in custom is null ? metadata.Parameters.Where(parameter => !IsBody(parameter)) : [])
        {
            var contractMember =
                PortiaJsonDescriptions.MemberMetadata(jsonOptions, metadata.RequestType, parameter.ClrName);
            var schema = PortiaOpenApiSchemaGenerator.Create(parameter.Type, jsonOptions, context.Document!, schemaType,
                parameter.IsNullable, schemaCollectionElementIsNullable, descriptions, contractMember, input: true);
            if (parameter.HasDefault && schema is OpenApiSchema concreteSchema)
            {
                concreteSchema.Default = contractMember?.Rules.Any(rule =>
                    !PortiaContractValidation.IsValid(contractMember, rule, parameter.DefaultValue)) == true
                    ? null
                    : PortiaOpenApiEnrichment.Scalar(parameter.DefaultValue, concreteSchema.Default, schema);
            }

            var headerName = HeaderName(parameter.ClrName);
            var wireName = headerName ?? parameter.WireName ??
                jsonOptions.PropertyNamingPolicy?.ConvertName(parameter.ClrName) ?? parameter.ClrName;
            operation.Parameters.Add(new OpenApiParameter
            {
                Description = MemberDescription(parameter, wireName),
                Name = wireName,
                In = headerName is not null ? ParameterLocation.Header
                    : parameter.Source == "route" ? ParameterLocation.Path : ParameterLocation.Query,
                Required = parameter.Source == "route" || parameter.Required || PortiaJsonConstraints.RequiresPresence(
                    PortiaJsonDescriptions.MemberMetadata(jsonOptions, metadata.RequestType, parameter.ClrName)),
                Schema = schema
            });
        }

        var accepts = endpointMetadata.OfType<PortiaStreamAcceptsMetadata>()
            .Select(item => new DeclaredAccepts(item.RequestType, item.IsOptional, item.ContentTypes))
            .SingleOrDefault() ?? endpointMetadata.OfType<IAcceptsMetadata>()
            .Select(item => new DeclaredAccepts(item.RequestType, item.IsOptional, item.ContentTypes.ToArray()))
            .LastOrDefault();
        var body = custom is null
            ? metadata.Parameters.Select((parameter, index) => (Parameter: parameter, Index: index))
                .Where(member => IsBody(member.Parameter)).ToArray()
            : [];
        if (custom is not null && accepts?.RequestType is not null)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = !accepts.IsOptional,
                Content = accepts.ContentTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(
                    contentType => contentType,
                    _ => (IOpenApiMediaType)new OpenApiMediaType
                    {
                        Schema = PortiaOpenApiSchemaGenerator.Create(accepts.RequestType, jsonOptions,
                            context.Document!, schemaType,
                            schemaCollectionElementIsNullable: schemaCollectionElementIsNullable,
                            descriptions: descriptions, input: true)
                    }, StringComparer.Ordinal)
            };
        }

        if (body.Length > 0)
        {
            var properties = new Dictionary<string, IOpenApiSchema>();
            var formProperties = new Dictionary<string, IOpenApiSchema>();
            var jsonRequired = new HashSet<string>();
            var formRequired = new HashSet<string>();
            foreach (var (parameter, index) in body)
            {
                var fallbackName = parameter.WireName ??
                                   jsonOptions.PropertyNamingPolicy?.ConvertName(parameter.ClrName) ??
                                   parameter.ClrName;
                var member = metadata.RequestType is { } requestType
                    ? PortiaHttpBinding.ResolveBodyMember(jsonOptions, requestType, parameter.Type, index,
                        parameter.ClrName, fallbackName)
                    : new PortiaHttpBinding.BindingMetadata(fallbackName, jsonOptions);
                var memberSchema = PortiaOpenApiSchemaGenerator.Create(parameter.Type, member.Options,
                    context.Document!,
                    schemaType, parameter.IsNullable, schemaCollectionElementIsNullable, descriptions,
                    PortiaJsonDescriptions.MemberMetadata(jsonOptions, metadata.RequestType, parameter.ClrName),
                    input: true);
                if (member.Input)
                    properties[member.Name] = PortiaOpenApiDescriptions.Annotate(memberSchema,
                        MemberDescription(parameter, member.Name));
                // Forms and queries use generated scalar parsing and their declared field names.
                // JSON member converters and resolver renames apply only to the JSON body.
                formProperties[fallbackName] = ReferenceEquals(member.Options, jsonOptions)
                    ? memberSchema
                    : PortiaOpenApiSchemaGenerator.Create(parameter.Type, jsonOptions, context.Document!, schemaType,
                        parameter.IsNullable, schemaCollectionElementIsNullable, descriptions);
                formProperties[fallbackName] = PortiaOpenApiDescriptions.Annotate(formProperties[fallbackName],
                    MemberDescription(parameter, fallbackName));
                if (member.Input && (parameter.Required || member.Required || PortiaJsonConstraints.RequiresPresence(
                        PortiaJsonDescriptions.MemberMetadata(jsonOptions, metadata.RequestType, parameter.ClrName))))
                    _ = jsonRequired.Add(member.Name);
                if (parameter.Required && parameter.Type != typeof(bool) || PortiaJsonConstraints.RequiresPresence(
                        PortiaJsonDescriptions.MemberMetadata(jsonOptions, metadata.RequestType, parameter.ClrName)))
                    _ = formRequired.Add(fallbackName);
            }

            var schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = metadata.RequestType is { } describedRequest
                    ? descriptions.Schema(describedRequest, jsonOptions)
                    : null,
                Title = metadata.RequestType is { } titledRequest
                    ? PortiaJsonDescriptions.Metadata(jsonOptions, titledRequest)?.Title
                    : null,
                Deprecated = metadata.RequestType is { } deprecatedRequest &&
                             PortiaJsonDescriptions.Metadata(jsonOptions, deprecatedRequest)?.Deprecated == true,
                Properties = properties,
                Required = jsonRequired.Count == 0 ? null : jsonRequired
            };
            Dictionary<string, IOpenApiMediaType> content = new()
            { ["application/json"] = new OpenApiMediaType { Schema = schema } };
            if (endpointMetadata.OfType<PortiaFormBindableBody>().Any() &&
                PortiaHttpBinding.AcceptsFormBody(endpointMetadata, context.ApplicationServices))
            {
                var formSchema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Description = schema.Description,
                    Title = schema.Title,
                    Deprecated = schema.Deprecated,
                    Properties = formProperties,
                    Required = formRequired.Count == 0 ? null : formRequired
                };
                content["application/x-www-form-urlencoded"] = new OpenApiMediaType { Schema = formSchema };
                content["multipart/form-data"] = new OpenApiMediaType { Schema = formSchema };
            }

            operation.RequestBody = new OpenApiRequestBody
            {
                Required = jsonRequired.Count > 0,
                Content = content
            };
        }

        if (replacesDefaultSuccess || createdLocation is not null)
        {
            // Explicit create and custom result responses replace the default success response.
        }
        else if (metadata.JsonStream || metadata.ServerSentEvents)
        {
            var item = PortiaOpenApiSchemaGenerator.Create(metadata.ResultType!, jsonOptions, context.Document!,
                schemaType,
                metadata.ResultIsNullable, schemaCollectionElementIsNullable, descriptions);
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
            var schema = PortiaOpenApiSchemaGenerator.Create(metadata.ResultType!, jsonOptions, context.Document!,
                schemaType,
                metadata.ResultIsNullable, schemaCollectionElementIsNullable, descriptions);
            SetDefaultResponse("200", Response("OK", "application/json", schema));
        }

        if (metadata.QueueCapable && customResult is null)
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Prefer",
                Description = "Use respond-async to enqueue the request instead of awaiting its result.",
                In = ParameterLocation.Header,
                Required = false,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
            SetDefaultResponse("202", Response("Accepted", "application/json", new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Enqueue acceptance receipt. Crash recovery depends on the queue provider. The request ID is for correlation.",
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    [jsonOptions.PropertyNamingPolicy?.ConvertName("RequestId") ?? "RequestId"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Format = "uuid",
                        Description = "Correlation identifier for the accepted request."
                    }
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

        await descriptions.ApplySecurityAsync(operation, metadata.RequestType, endpointMetadata,
            context.ApplicationServices,
            context.Document!, cancellationToken).ConfigureAwait(false);
        endpointDescriptions?.ApplyResponses(operation);
        (endpointMetadata.OfType<PortiaOpenApiEnrichment>().SingleOrDefault() ?? new([], [], []))
            .Apply(operation, metadata, endpointMetadata, context.ApplicationServices, jsonOptions, context.Document!,
                httpOptions);
        return;
    }

    static OpenApiResponse Response(string description, string mediaType, IOpenApiSchema schema) => new()
    {
        Description = description,
        Content = new Dictionary<string, IOpenApiMediaType>
        { [mediaType] = new OpenApiMediaType { Schema = schema } }
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
                    var responses = components.Responses ??=
                        new Dictionary<string, IOpenApiResponse>(StringComparer.Ordinal);
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
                Description = "Details of a failed HTTP request.",
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["type"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Format = "uri-reference",
                        Description = "URI identifying the problem type."
                    },
                    ["title"] = new OpenApiSchema
                    { Type = JsonSchemaType.String, Description = "Short description of the problem." },
                    ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Description = "HTTP status code." },
                    ["detail"] = new OpenApiSchema
                    { Type = JsonSchemaType.String, Description = "Explanation specific to this occurrence." },
                    ["instance"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Format = "uri-reference",
                        Description = "URI identifying this occurrence."
                    },
                    ["transient"] = new OpenApiSchema
                    { Type = JsonSchemaType.Boolean, Description = "Whether retrying may succeed." }
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

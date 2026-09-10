using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

sealed class PortiaOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata?.OfType<PortiaOpenApiOperation>()
            .SingleOrDefault();
        if (metadata == null)
            return;

        operation.Responses ??= [];
        var explicitStatuses = context.Description.ActionDescriptor.EndpointMetadata?
            .OfType<IProducesResponseTypeMetadata>()
            .Select(item => item.StatusCode.ToString(CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var status in operation.Responses.Keys.Where(status => !explicitStatuses.Contains(status)).ToArray())
            _ = operation.Responses.Remove(status);

        void SetDefaultResponse(string status, IOpenApiResponse response)
        {
            if (!explicitStatuses.Contains(status))
                operation.Responses[status] = response;
        }

        operation.OperationId = metadata.OperationId;
        operation.Parameters = [];
        var jsonOptions = context.ApplicationServices.GetRequiredService<JsonSerializerOptions>();
        foreach (var parameter in metadata.Parameters.Where(parameter => parameter.Source != "body"))
        {
            var schema = await context.GetOrCreateSchemaAsync(parameter.Type, null, cancellationToken)
                .ConfigureAwait(false);
            if (parameter.HasDefault && schema is OpenApiSchema concreteSchema)
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

        var body = metadata.Parameters.Where(parameter => parameter.Source == "body").ToArray();
        if (body.Length > 0)
        {
            var schema = new OpenApiSchema
            { Type = JsonSchemaType.Object, Properties = new Dictionary<string, IOpenApiSchema>() };
            foreach (var parameter in body)
            {
                var name = parameter.WireName ?? jsonOptions.PropertyNamingPolicy?.ConvertName(parameter.ClrName) ??
                    parameter.ClrName;
                schema.Properties[name] = await context.GetOrCreateSchemaAsync(parameter.Type, null, cancellationToken)
                    .ConfigureAwait(false);
                if (parameter.Required)
                {
                    schema.Required ??= new HashSet<string>();
                    _ = schema.Required.Add(name);
                }
            }

            operation.RequestBody = new OpenApiRequestBody
            {
                Required = body.Any(parameter => parameter.Required),
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } }
            };
        }

        if (metadata.JsonStream || metadata.ServerSentEvents)
        {
            var item = await context.GetOrCreateSchemaAsync(metadata.ResultType!, null, cancellationToken)
                .ConfigureAwait(false);
            IOpenApiSchema schema = metadata.JsonStream
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
            var schema = await context.GetOrCreateSchemaAsync(metadata.ResultType!, null, cancellationToken)
                .ConfigureAwait(false);
            SetDefaultResponse("200", Response("OK", "application/json", schema));
        }

        if (metadata.QueueCapable)
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

        foreach (var status in new[] { "400", "401", "403", "404", "409", "413", "500" })
        {
            var response = status == "401"
                ? new OpenApiResponse
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
                }
                : Response("Error", "application/problem+json", new OpenApiSchema
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
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            _ = response.Headers.TryAdd(ResultHttpExtensions.TransientHeaderName, new OpenApiHeader
            {
                Required = status == "401",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean }
            });
            SetDefaultResponse(status, response);
        }
    }

    static OpenApiResponse Response(string description, string mediaType, IOpenApiSchema schema) => new()
    {
        Description = description,
        Content = new Dictionary<string, OpenApiMediaType> { [mediaType] = new() { Schema = schema } }
    };
}

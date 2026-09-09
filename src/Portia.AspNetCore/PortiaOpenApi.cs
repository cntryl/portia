using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

/// <summary>Framework-owned bootstrap used by generated application interceptors.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class PortiaOpenApi
{
    const string DocumentName = "v1";

    /// <summary>Registers Portia's OpenAPI document and builds the application.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddServices(builder.Services);
        return Map(builder.Build());
    }

    /// <summary>Creates an application with Portia's OpenAPI document.</summary>
    public static WebApplication Create(string[]? args)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        AddServices(builder.Services);
        return Map(builder.Build());
    }

    static void AddServices(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(PortiaOpenApiMarker)))
            return;
        _ = services.AddSingleton<PortiaOpenApiMarker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<JsonOptions>, PortiaOpenApiJsonOptions>());
        _ = services.AddOpenApi(DocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            _ = options.AddOperationTransformer<PortiaOpenApiOperationTransformer>();
            _ = options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                _ = cancellationToken;
                document.Info.Title = context.ApplicationServices.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().ApplicationName;
                document.Info.Version = DocumentName;
                var duplicate = document.Paths
                    .SelectMany(path => path.Value.Operations ?? [])
                    .Select(operation => operation.Value.OperationId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .GroupBy(id => id, StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1);
                _ = duplicate is null ? true
                    : throw new InvalidOperationException($"OpenAPI operationId '{duplicate.Key}' is duplicated; the document cannot be served.");
                return Task.CompletedTask;
            });
        });
    }

    static WebApplication Map(WebApplication app)
    {
        _ = app.MapOpenApi("/openapi/{documentName}.json");
        _ = app.MapOpenApi("/openapi/{documentName}.yml");
        return app;
    }

    sealed class PortiaOpenApiMarker;
}

sealed class PortiaOpenApiJsonOptions(JsonSerializerOptions portia) : IConfigureOptions<JsonOptions>
{
    public void Configure(JsonOptions options)
    {
        var target = options.SerializerOptions;
        target.AllowOutOfOrderMetadataProperties = portia.AllowOutOfOrderMetadataProperties;
        target.AllowTrailingCommas = portia.AllowTrailingCommas;
        target.DefaultBufferSize = portia.DefaultBufferSize;
        target.Encoder = portia.Encoder;
        target.IgnoreReadOnlyFields = portia.IgnoreReadOnlyFields;
        target.IgnoreReadOnlyProperties = portia.IgnoreReadOnlyProperties;
        target.IncludeFields = portia.IncludeFields;
        target.MaxDepth = portia.MaxDepth;
        target.NewLine = portia.NewLine;
        target.PropertyNamingPolicy = portia.PropertyNamingPolicy;
        target.DictionaryKeyPolicy = portia.DictionaryKeyPolicy;
        target.PropertyNameCaseInsensitive = portia.PropertyNameCaseInsensitive;
        target.NumberHandling = portia.NumberHandling;
        target.DefaultIgnoreCondition = portia.DefaultIgnoreCondition;
        target.PreferredObjectCreationHandling = portia.PreferredObjectCreationHandling;
        target.ReadCommentHandling = portia.ReadCommentHandling;
        target.ReferenceHandler = portia.ReferenceHandler;
        target.RespectNullableAnnotations = portia.RespectNullableAnnotations;
        target.RespectRequiredConstructorParameters = portia.RespectRequiredConstructorParameters;
        target.UnknownTypeHandling = portia.UnknownTypeHandling;
        target.UnmappedMemberHandling = portia.UnmappedMemberHandling;
        target.WriteIndented = portia.WriteIndented;
        target.TypeInfoResolver = portia.TypeInfoResolver;
        target.Converters.Clear();
        foreach (var converter in portia.Converters)
            target.Converters.Add(converter);
    }
}

/// <summary>Compile-time endpoint shape consumed by Portia's Microsoft OpenAPI transformer.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record PortiaOpenApiOperation(
    string OperationId,
    Type? ResultType,
    bool NoContent,
    bool JsonStream,
    bool ServerSentEvents,
    bool QueueCapable,
    IReadOnlyList<PortiaOpenApiParameter> Parameters);

/// <summary>One generated constructor binding in a Portia HTTP operation.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record PortiaOpenApiParameter(string ClrName, Type Type, string Source, bool Required, bool HasDefault, object? DefaultValue, string? WireName);

sealed class PortiaOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata?.OfType<PortiaOpenApiOperation>().SingleOrDefault();
        if (metadata is null)
            return;

        operation.OperationId = metadata.OperationId;
        operation.Parameters = [];
        var jsonOptions = context.ApplicationServices.GetRequiredService<JsonSerializerOptions>();
        foreach (var parameter in metadata.Parameters.Where(parameter => parameter.Source != "body"))
        {
            var schema = await context.GetOrCreateSchemaAsync(parameter.Type, null, cancellationToken).ConfigureAwait(false);
            if (parameter.HasDefault && schema is OpenApiSchema concreteSchema)
                concreteSchema.Default = JsonSerializer.SerializeToNode(parameter.DefaultValue, jsonOptions.GetTypeInfo(parameter.Type));
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = parameter.WireName ?? jsonOptions.PropertyNamingPolicy?.ConvertName(parameter.ClrName) ?? parameter.ClrName,
                In = parameter.Source == "route" ? ParameterLocation.Path : ParameterLocation.Query,
                Required = parameter.Source == "route" || parameter.Required,
                Schema = schema,
            });
        }

        var body = metadata.Parameters.Where(parameter => parameter.Source == "body").ToArray();
        if (body.Length > 0)
        {
            var schema = new OpenApiSchema { Type = JsonSchemaType.Object, Properties = new Dictionary<string, IOpenApiSchema>() };
            foreach (var parameter in body)
            {
                var name = parameter.WireName ?? jsonOptions.PropertyNamingPolicy?.ConvertName(parameter.ClrName) ?? parameter.ClrName;
                schema.Properties[name] = await context.GetOrCreateSchemaAsync(parameter.Type, null, cancellationToken).ConfigureAwait(false);
                if (parameter.Required)
                {
                    schema.Required ??= new HashSet<string>();
                    _ = schema.Required.Add(name);
                }
            }
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = body.Any(parameter => parameter.Required),
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } },
            };
        }

        operation.Responses ??= [];
        operation.Responses.Clear();
        if (metadata.JsonStream || metadata.ServerSentEvents)
        {
            var item = await context.GetOrCreateSchemaAsync(metadata.ResultType!, null, cancellationToken).ConfigureAwait(false);
            IOpenApiSchema schema = metadata.JsonStream ? new OpenApiSchema { Type = JsonSchemaType.Array, Items = item } : item;
            operation.Responses["200"] = Response("OK", metadata.ServerSentEvents ? "text/event-stream" : "application/json", schema);
        }
        else if (metadata.NoContent)
        {
            operation.Responses["204"] = new OpenApiResponse { Description = "No Content" };
        }
        else
        {
            var schema = await context.GetOrCreateSchemaAsync(metadata.ResultType!, null, cancellationToken).ConfigureAwait(false);
            operation.Responses["200"] = Response("OK", "application/json", schema);
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
            operation.Responses["202"] = new OpenApiResponse { Description = "Accepted" };
        }
        foreach (var status in new[] { "400", "401", "403", "404", "409", "500" })
        {
            operation.Responses[status] = status == "401" ? new OpenApiResponse { Description = "Unauthorized" }
                : Response("Error", "application/problem+json", new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema> { ["message"] = new OpenApiSchema { Type = JsonSchemaType.String } },
                    Required = new HashSet<string> { "message" },
                });
        }
    }

    static OpenApiResponse Response(string description, string mediaType, IOpenApiSchema schema) => new()
    {
        Description = description,
        Content = new Dictionary<string, OpenApiMediaType> { [mediaType] = new() { Schema = schema } },
    };
}

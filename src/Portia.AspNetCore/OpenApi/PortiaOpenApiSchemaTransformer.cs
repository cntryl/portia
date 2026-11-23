using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

// ASP.NET can retain its own schema components for explicit Accepts/Produces declarations.
// Describe those too, so coverage does not require hand-written transformers for those components.
sealed class PortiaOpenApiSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptions = context.ApplicationServices.GetRequiredService<IOptions<PortiaHttpOptions>>().Value.OpenApi;
        var json = context.ApplicationServices.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
        var text = (context.JsonPropertyInfo is { } property
                       ? descriptions.Property(property, context.JsonTypeInfo.Options, json)
                       : null)
                   ?? descriptions.Schema(context.JsonTypeInfo.Type, json);

        if (text is not null)
            schema.Description = text;

        var member = context.JsonPropertyInfo is { } describedProperty
            ? PortiaJsonDescriptions.PropertyMetadata(json, describedProperty)
            : null;
        var contract = PortiaJsonDescriptions.Metadata(json, context.JsonTypeInfo.Type);
        schema.Title = member?.Title ?? contract?.Title ?? schema.Title;
        schema.Deprecated = member?.Deprecated == true || contract?.Deprecated == true || schema.Deprecated;

        foreach (var (name, child) in schema.Properties?.ToArray() ?? [])
        {
            if (child.Description is null)
                schema.Properties![name] = PortiaOpenApiDescriptions.Annotate(child, descriptions.SharedMember(name));
        }


        return Task.CompletedTask;
    }
}

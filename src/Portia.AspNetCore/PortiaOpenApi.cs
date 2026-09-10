using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        {
            return;
        }

        _ = services.AddSingleton<PortiaOpenApiMarker>();
        _ = services.AddOptions<PortiaHttpOptions>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<JsonOptions>, PortiaOpenApiJsonOptions>());
        _ = services.AddOpenApi(DocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            _ = options.AddOperationTransformer<PortiaOpenApiOperationTransformer>();
            _ = options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                _ = cancellationToken;
                document.Info.Title =
                    context.ApplicationServices.GetRequiredService<IHostEnvironment>().ApplicationName;
                document.Info.Version = DocumentName;
                var duplicate = document.Paths
                    .SelectMany(path => path.Value.Operations ?? [])
                    .Select(operation => operation.Value.OperationId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .GroupBy(id => id, StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1);
                _ = duplicate is null
                    ? true
                    : throw new InvalidOperationException(
                        $"OpenAPI operationId '{duplicate.Key}' is duplicated; the document cannot be served.");
                return Task.CompletedTask;
            });
        });
    }

    static WebApplication Map(WebApplication app)
    {
        _ = app.MapOpenApi();
        _ = app.MapOpenApi("/openapi/{documentName}.yml");
        return app;
    }

    sealed class PortiaOpenApiMarker;
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

/// <summary>Explicitly activates Portia's generated HTTP and OpenAPI integration.</summary>
public static class PortiaHttpExtensions
{
    const string DocumentName = "v1";

    static readonly string[] Get = ["GET"];

    /// <summary>
    ///     Adds Portia's HTTP and OpenAPI services. Every generated HTTP endpoint requires them, and they let
    ///     cross-origin protection honor the application's CORS pipeline.
    /// </summary>
    /// <param name="builder">The Portia composition root.</param>
    /// <param name="configure">Optional HTTP-boundary configuration.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static PortiaBuilder AddHttp(this PortiaBuilder builder, Action<PortiaHttpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        _ = configure is null
            ? services.AddOptions<PortiaHttpOptions>()
            : services.AddOptions<PortiaHttpOptions>().Configure(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(PortiaOpenApiMarker)))
            return builder;

        _ = services.AddSingleton<PortiaOpenApiMarker>();
        PortiaCrossOrigin.AddCorsDecisions(services);
        _ = services.AddSingleton(provider => new PortiaOpenApiDocumentCache(provider, DocumentName));
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
        return builder;
    }

    /// <summary>
    ///     Applies Portia's fail-closed authorization baseline to the request bus and generated HTTP endpoints.
    ///     The host must configure a default authentication scheme before it starts.
    /// </summary>
    /// <param name="builder">The Portia composition root.</param>
    /// <param name="configure">Allows named requests or request families to dispatch anonymously.</param>
    /// <returns>The same builder for further composition.</returns>
    public static PortiaBuilder RequireSecureHost(this PortiaBuilder builder,
        Action<PortiaAuthorizationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.RequireAuthorization(configure);
        builder.AddHttp();
        _ = builder.Services.AddAuthentication();
        _ = builder.Services.AddAuthorization();
        builder.Services.TryAddSingleton<PortiaSecureHostMarker>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, PortiaHttpSecurityStartupValidator>());
        return builder;
    }

    /// <summary>Maps Portia's OpenAPI 3.1 document as JSON and YAML.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same endpoint route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapPortiaOpenApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Mapped as request delegates, which keeps RequestDelegateFactory out of an AOT build and
        // leaves these two routes out of the very document they serve.
        var cache = app.ServiceProvider.GetRequiredService<PortiaOpenApiDocumentCache>();
        var json = app.MapMethods($"/openapi/{DocumentName}.json", Get,
            (RequestDelegate)(context => cache.WriteAsync(context, false)));
        var yaml = app.MapMethods($"/openapi/{DocumentName}.yml", Get,
            (RequestDelegate)(context => cache.WriteAsync(context, true)));
        PortiaHttpBinding.ApplySecureHostAuthorization(app, json);
        PortiaHttpBinding.ApplySecureHostAuthorization(app, yaml);
        return app;
    }

    internal sealed class PortiaOpenApiMarker;
}

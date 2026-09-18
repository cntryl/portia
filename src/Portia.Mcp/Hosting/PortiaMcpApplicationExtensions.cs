using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol.Server;

namespace Cntryl.Portia;

/// <summary>Adds MCP exposure to the shared Portia application composition.</summary>
public static class PortiaMcpApplicationExtensions
{
    static readonly ConditionalWeakTable<PortiaBuilder, ToolCatalog> Catalogs = [];

    /// <summary>Exposes one callable request as an MCP tool.</summary>
    /// <typeparam name="TRequest">The concrete callable request.</typeparam>
    /// <param name="application">The shared Portia application.</param>
    /// <param name="configure">Optional MCP presentation metadata.</param>
    /// <returns>The same application builder for chaining.</returns>
    public static PortiaBuilder AddMcpTool<TRequest>(this PortiaBuilder application,
        Action<McpToolOptions>? configure = null)
        where TRequest : IRequestBase, ICallable
    {
        ArgumentNullException.ThrowIfNull(application);
        throw new InvalidOperationException(
            "Portia.Generators did not intercept this MCP tool registration. Ensure analyzer assets are enabled.");
    }

    /// <summary>Adds a generated no-result tool descriptor.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static PortiaBuilder AddGeneratedMcpTool<TRequest>(this PortiaBuilder application, string name,
        string description, Action<McpToolOptions>? configure)
        where TRequest : IRequest, ICallable
        => AddGenerated(application, new McpToolRegistration<TRequest>(name, description, Options(configure)));

    /// <summary>Adds a generated result-bearing tool descriptor.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static PortiaBuilder AddGeneratedMcpTool<TRequest, TOut>(this PortiaBuilder application, string name,
        string description, Action<McpToolOptions>? configure)
        where TRequest : IRequest<TOut>, ICallable
        => AddGenerated(application, new McpToolRegistration<TRequest, TOut>(name, description, Options(configure)));

    static McpToolOptions Options(Action<McpToolOptions>? configure)
    {
        var options = new McpToolOptions();
        configure?.Invoke(options);
        return options;
    }

    static PortiaBuilder AddGenerated(PortiaBuilder application, McpToolRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(application);
        var catalog = Catalogs.GetValue(application, static _ => new ToolCatalog());
        lock (catalog.Registrations)
        {
            if (catalog.Registrations.TryGetValue(registration.RequestType, out var existing))
            {
                return Equivalent(existing, registration)
                    ? application
                    : throw new InvalidOperationException(
                        $"Request '{registration.RequestType}' has conflicting MCP tool declarations.");
            }

            if (catalog.Names.TryGetValue(registration.Name, out var owner))
                throw new InvalidOperationException(
                    $"MCP tool name '{registration.Name}' is already owned by request '{owner}'.");
            catalog.Registrations.Add(registration.RequestType, registration);
            catalog.Names.Add(registration.Name, registration.RequestType);
            _ = application.Services.AddSingleton<McpToolRegistration>(registration);
            application.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, McpStartupValidator>());
            _ = application.Services.AddSingleton<McpServerTool>(services =>
                new PortiaMcpServerTool(registration,
                    services.GetRequiredService<JsonSerializerOptions>(),
                    services.GetService<ILogger<PortiaMcpServerTool>>()
                    ?? NullLogger<PortiaMcpServerTool>.Instance));
        }

        return application;
    }

    /// <summary>Activates the declared Portia MCP tools over the standard input/output transport.</summary>
    public static PortiaBuilder AddMcpStdio(this PortiaBuilder application,
        Action<McpStdioOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        configure?.Invoke(new McpStdioOptions(application.Services));
        if (!application.Services.Any(service => service.ServiceType == typeof(McpToolRegistration)))
            throw new InvalidOperationException(
                "AddMcpStdio requires at least one AddMcpTool<TRequest>() declaration.");
        if (!application.Services.Any(service => service.ServiceType == typeof(IMcpActorProvider)))
            throw new InvalidOperationException(
                "AddMcpStdio requires an explicit actor policy. Configure UseActorProvider<TProvider>() "
                + "or UseLocalDevelopmentActor().");
        application.Services.Configure<ConsoleLoggerOptions>(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace);
        _ = application.Services.AddMcpServer().WithStdioServerTransport();
        return application;
    }

    static bool Equivalent(McpToolRegistration left, McpToolRegistration right) =>
        left.Name == right.Name && left.Description == right.Description && left.Title == right.Title
        && left.ReadOnly == right.ReadOnly && left.Destructive == right.Destructive
        && left.Idempotent == right.Idempotent && left.OpenWorld == right.OpenWorld;

    sealed class ToolCatalog
    {
        internal Dictionary<Type, McpToolRegistration> Registrations { get; } = [];
        internal Dictionary<string, Type> Names { get; } = new(StringComparer.Ordinal);
    }
}

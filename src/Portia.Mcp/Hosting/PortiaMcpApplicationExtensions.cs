using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
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
    static readonly ConditionalWeakTable<PortiaBuilder, McpPrimitiveCatalog> PrimitiveCatalogs = [];

    /// <summary>Exposes one callable request as a fixed or templated MCP resource.</summary>
    public static PortiaBuilder AddMcpResource<TRequest, TOut>(this PortiaBuilder application,
        string uriTemplate, Func<IReadOnlyDictionary<string, string>, TRequest> bind,
        Action<McpResourceOptions<TOut>> configure)
        where TRequest : IRequest<TOut>, ICallable
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(configure);
        McpPrimitiveHandlers.RequireDeclarationsBeforeTransport(application.Services);
        McpUri.ValidateTemplate(uriTemplate);
        var configuredLimits = application.Services.FirstOrDefault(service => service.ServiceType == typeof(McpLimits))
            ?.ImplementationInstance as McpLimits;
        if (System.Text.Encoding.UTF8.GetByteCount(uriTemplate) >
            (configuredLimits?.MaxResourceUriBytes ?? new McpLimits().MaxResourceUriBytes))
            throw new ArgumentException("Resource URI template exceeds the configured URI limit.", nameof(uriTemplate));
        var options = new McpResourceOptions<TOut>();
        configure(options);
        if (options.Access == McpAccess.Unset || options.MediaType is null)
            throw new ArgumentException("Resource requires access and a content projection.", nameof(configure));
        var catalog = PrimitiveCatalogs.GetValue(application, static _ => new McpPrimitiveCatalog());
        foreach (var existing in catalog.Resources)
        {
            if (CanOverlap(existing.Template, uriTemplate))
                throw new InvalidOperationException("Ambiguous MCP resource registration.");
        }
        var entry = new McpResourceEntry<TRequest, TOut>(uriTemplate, bind, options);
        catalog.Resources.Add(entry);
        _ = application.Services.AddSingleton<McpResourceEntry>(entry);
        RegisterPrimitives(application);
        return application;

        static bool CanOverlap(string left, string right)
        {
            var a = left.Split('/');
            var b = right.Split('/');
            return a.Length == b.Length && a.Zip(b).All(pair =>
                pair.First.StartsWith('{') || pair.Second.StartsWith('{') ||
                Uri.UnescapeDataString(pair.First) == Uri.UnescapeDataString(pair.Second));
        }
    }

    /// <summary>Exposes one callable request as an MCP prompt.</summary>
    public static PortiaBuilder AddMcpPrompt<TRequest, TOut>(this PortiaBuilder application,
        string name, Func<IReadOnlyDictionary<string, string>, TRequest> bind,
        Func<TOut, IReadOnlyList<McpPromptMessage>> render, Action<McpPromptOptions> configure)
        where TRequest : IRequest<TOut>, ICallable
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(configure);
        McpPrimitiveHandlers.RequireDeclarationsBeforeTransport(application.Services);
        var options = new McpPromptOptions();
        configure(options);
        if (options.Access == McpAccess.Unset)
            throw new ArgumentException("Prompt requires access.", nameof(configure));
        var catalog = PrimitiveCatalogs.GetValue(application, static _ => new McpPrimitiveCatalog());
        if (catalog.Prompts.Any(prompt => prompt.Name == name))
            throw new InvalidOperationException("Duplicate MCP prompt registration.");
        var entry = new McpPromptEntry<TRequest, TOut>(name, bind, render, options);
        catalog.Prompts.Add(entry);
        _ = application.Services.AddSingleton<McpPromptEntry>(entry);
        RegisterPrimitives(application);
        return application;
    }

    /// <summary>Configures MCP ingress limits.</summary>
    public static PortiaBuilder ConfigureMcpLimits(this PortiaBuilder application, Action<McpLimits> configure)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(configure);
        var descriptor = application.Services.FirstOrDefault(service => service.ServiceType == typeof(McpLimits));
        var existing = descriptor?.ImplementationInstance as McpLimits;
        var proposed = new McpLimits();
        if (existing is not null)
            CopyLimits(existing, proposed);
        configure(proposed);
        if (proposed.MaxResourceUriBytes <= 0 || proposed.MaxPromptArgumentBytes <= 0 ||
            proposed.MaxPromptMessages <= 0 || proposed.MaxResultBytes <= 0 || proposed.OperationDeadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configure), "MCP limits must be positive.");
        if (PrimitiveCatalogs.TryGetValue(application, out var catalog) &&
            catalog.Resources.Any(resource => Encoding.UTF8.GetByteCount(resource.Template) > proposed.MaxResourceUriBytes))
            throw new ArgumentOutOfRangeException(nameof(configure),
                "The MCP resource URI limit is shorter than a registered resource URI template.");
        if (existing is not null)
            CopyLimits(proposed, existing);
        else
            _ = application.Services.AddSingleton(proposed);
        return application;
    }

    static void CopyLimits(McpLimits source, McpLimits target)
    {
        target.MaxResourceUriBytes = source.MaxResourceUriBytes;
        target.MaxPromptArgumentBytes = source.MaxPromptArgumentBytes;
        target.MaxPromptMessages = source.MaxPromptMessages;
        target.MaxResultBytes = source.MaxResultBytes;
        target.OperationDeadline = source.OperationDeadline;
    }

    static void RegisterPrimitives(PortiaBuilder application)
    {
        application.Services.TryAddSingleton(new McpLimits());
        application.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, McpStartupValidator>());
    }

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
        application.Services.TryAddSingleton(new McpLimits());
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
                    services.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json),
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
        if (!HasDeclarations(application.Services))
            throw new InvalidOperationException(
                "AddMcpStdio requires at least one MCP declaration.");
        if (!application.Services.Any(service => service.ServiceType == typeof(IMcpActorProvider)))
            throw new InvalidOperationException(
                "AddMcpStdio requires an explicit actor policy. Configure UseActorProvider<TProvider>() "
                + "or UseLocalDevelopmentActor().");
        application.Services.Configure<ConsoleLoggerOptions>(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace);
        var server = application.Services.AddMcpServer().WithStdioServerTransport();
        McpPrimitiveHandlers.Attach(server, application.Services);
        McpPrimitiveHandlers.MarkTransportActivated(application.Services, "AddMcpStdio");
        return application;
    }

    internal static bool HasDeclarations(IServiceCollection services) =>
        services.Any(service => service.ServiceType == typeof(McpToolRegistration)
                                || service.ServiceType == typeof(McpResourceEntry)
                                || service.ServiceType == typeof(McpPromptEntry));

    static bool Equivalent(McpToolRegistration left, McpToolRegistration right) =>
        left.Name == right.Name && left.Description == right.Description && left.Title == right.Title
        && left.ReadOnly == right.ReadOnly && left.Destructive == right.Destructive
        && left.Idempotent == right.Idempotent && left.OpenWorld == right.OpenWorld;

    sealed class ToolCatalog
    {
        internal Dictionary<Type, McpToolRegistration> Registrations { get; } = [];
        internal Dictionary<string, Type> Names { get; } = new(StringComparer.Ordinal);
    }

    sealed class McpPrimitiveCatalog
    {
        internal List<McpResourceEntry> Resources { get; } = [];
        internal List<McpPromptEntry> Prompts { get; } = [];
    }
}

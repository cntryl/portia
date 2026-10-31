using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cntryl.Portia;

/// <summary>Hosts an independent administrative MCP surface and non-MCP operator approvals.</summary>
public static class PortiaAdminMcpExtensions
{
    /// <summary>
    /// Adds stateless HTTP administration. Per-request options replace all application catalogs,
    /// handlers, filters and capabilities on endpoints with trusted administrative metadata.
    /// </summary>
    public static PortiaBuilder AddAdminMcpHttp(this PortiaBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (application.Services.Any(s => s.ServiceType == typeof(AdminSurface)))
            return application;
        application.Services.AddAuthorization();
        application.Services.AddHttpContextAccessor();
        application.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true);
        application.Services.AddSingleton<AdminSurface>();
        application.Services.PostConfigure<HttpServerTransportOptions>(transport =>
        {
            if (!transport.Stateless)
                throw new InvalidOperationException("Portia Admin MCP requires stateless shared HTTP transport.");
            var applicationConfiguration = transport.ConfigureSessionOptions;
            transport.ConfigureSessionOptions = async (context, server, ct) =>
            {
                if (context.GetEndpoint()?.Metadata.GetMetadata<AdminSurface>() is null)
                {
                    if (applicationConfiguration is not null)
                        await applicationConfiguration(context, server, ct).ConfigureAwait(false);
                    return;
                }
                SelectAdmin(server);
            };
        });
        return application;
    }

    /// <summary>Maps the isolated MCP surface; authentication and host operational policies are required.</summary>
    public static IEndpointConventionBuilder MapPortiaAdminMcp(this IEndpointRouteBuilder endpoints,
        string pattern = "/admin/mcp")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        _ = endpoints.ServiceProvider.GetRequiredService<AdminSurface>();
        _ = endpoints.ServiceProvider.GetRequiredService<IPortiaOperations>();
        _ = endpoints.ServiceProvider.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;
        var builder = endpoints.MapMcp(pattern).WithMetadata(new AdminSurface(), new AdminBodyLimit()).RequireAuthorization();
        builder.Finally(endpoint =>
        {
            if (endpoint.RequestDelegate is { } next)
                endpoint.RequestDelegate = context => RejectOrigin(context)
                    ? Results.StatusCode(StatusCodes.Status403Forbidden).ExecuteAsync(context) : next(context);
        });
        return builder;
    }

    /// <summary>
    /// Maps POST /{planId}/approve and POST /{planId}/revoke outside MCP. The body is an exact
    /// operational target; hosts must exclude approval permission from delegated agent credentials.
    /// </summary>
    public static IEndpointConventionBuilder MapPortiaRepairApprovals(this IEndpointRouteBuilder endpoints,
        string pattern = "/admin/repairs")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var group = endpoints.MapGroup(pattern).RequireAuthorization().WithMetadata(new AdminBodyLimit());
        group.MapPost("/{planId:guid}/approve", (RequestDelegate)(context => ApprovalRequestAsync(context, false)));
        group.MapPost("/{planId:guid}/revoke", (RequestDelegate)(context => ApprovalRequestAsync(context, true)));
        return group;
    }

    static async Task ApprovalRequestAsync(HttpContext context, bool revoke)
    {
        if (!Guid.TryParse(context.Request.RouteValues["planId"]?.ToString(), out var planId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var result = await ApproveAsync(context, planId, revoke).ConfigureAwait(false);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    static async Task<IResult> ApproveAsync(HttpContext context, Guid planId, bool revoke)
    {
        if (RejectOrigin(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            var target = await context.Request.ReadFromJsonAsync(AdminJsonContext.Default.OperationalTarget,
                context.RequestAborted).ConfigureAwait(false);
            if (target is null)
                return Results.BadRequest();
            var operations = context.RequestServices.GetRequiredService<IPortiaOperations>();
            if (revoke)
            {
                await operations.RevokeRepairAsync(context.User, target, planId, context.RequestAborted).ConfigureAwait(false);
                return Results.NoContent();
            }
            var approval = await operations.ApproveRepairAsync(context.User, target, planId, context.RequestAborted)
                .ConfigureAwait(false);
            return Results.Json(approval, AdminJsonContext.Default.RepairApproval);
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return Results.Conflict(); }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    static bool RejectOrigin(HttpContext context) => context.Request.Headers.Origin is { Count: > 0 } origins
        && (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            || !string.Equals(origin.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase));

    static void SelectAdmin(McpServerOptions server)
    {
        server.ServerInfo = new Implementation { Name = "Portia administration", Version = "1" };
        server.ServerInstructions = "Diagnose processing from durable evidence. Recovery approval is an operator action outside MCP. Processing completion is not a business consistency verdict.";
        server.ToolCollection = null;
        server.ResourceCollection = null;
        server.PromptCollection = null;
#pragma warning disable MCPEXP002 // Clear raw application handlers as well as ordinary MCP fallbacks.
        server.RequestHandlers = null;
#pragma warning restore MCPEXP002
        server.Filters = new McpServerFilters();
        server.Handlers = new McpServerHandlers
        {
            ListToolsHandler = (call, ct) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = ToolNames.Select(name => new Tool
                {
                    Name = name,
                    Description = Description(name),
                    InputSchema = InputSchema,
                    Annotations = new ToolAnnotations
                    {
                        ReadOnlyHint = name is not ("preview_repair" or "execute_repair"),
                        IdempotentHint = name != "preview_repair",
                        OpenWorldHint = false
                    }
                }).ToList()
            }),
            CallToolHandler = CallAsync
        };
        server.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } };
    }

    static readonly string[] ToolNames = ["describe_application", "get_system_health", "list_issues", "inspect_processor",
        "trace_causality", "preview_repair", "execute_repair", "verify_repair"];

    static string Description(string name) => name switch
    {
        "describe_application" => "Describe selected generated registrations; opaque dependencies are unknown.",
        "get_system_health" => "Read processing health for an exact tenant or global scope.",
        "list_issues" => "List durable processor blocks for an exact tenant or global scope.",
        "inspect_processor" => "Inspect a processor's retained execution and recovery evidence.",
        "trace_causality" => "Trace one recorded attempt with explicit local coverage limits.",
        "preview_repair" => "Preview an immutable recovery plan without invoking application handlers.",
        "execute_repair" => "Queue an operator-approved repair for its owning worker; never approves a plan.",
        _ => "Verify processing of the approved unit without asserting business success."
    };

    static readonly JsonElement InputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "scope":{"type":"object","properties":{"tenant":{"type":["string","null"]}},"required":["tenant"],"additionalProperties":false},
          "target":{"type":"object","properties":{"processor":{"type":"string"},"pattern":{"type":"string"},
             "scope":{"type":"object","properties":{"tenant":{"type":["string","null"]}},"required":["tenant"]},"rebuildId":{"type":["string","null"]}},"required":["processor","pattern","scope"]},
          "attemptId":{"type":"string","format":"uuid"},"planId":{"type":"string","format":"uuid"}},
          "required":["scope"],"additionalProperties":false}
        """).RootElement.Clone();

    static async ValueTask<CallToolResult> CallAsync(ModelContextProtocol.Server.RequestContext<CallToolRequestParams> call, CancellationToken ct)
    {
        if (!ToolNames.Contains(call.Params.Name, StringComparer.Ordinal))
            throw new McpException("Administrative tool unavailable.");
        var services = call.Services ?? throw new McpException("Request services unavailable.");
        var context = services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new McpException("Authenticated HTTP request required.");
        try
        {
            var arguments = JsonSerializer.SerializeToElement(call.Params.Arguments,
                AdminJsonContext.Default.IDictionaryStringJsonElement);
            if (!arguments.TryGetProperty("scope", out var scope)
                || !scope.TryGetProperty("tenant", out _))
                throw new ArgumentException("An exact tenant or global scope is required.");
            var input = arguments.Deserialize(AdminJsonContext.Default.AdminArguments)
                ?? throw new ArgumentException("Arguments required.");
            if (input.Target is { } target && target.Scope != input.Scope)
                throw new ArgumentException("The target and requested scope differ.");
            var operations = services.GetRequiredService<IPortiaOperations>();
            var actor = context.User;
            var json = call.Params.Name switch
            {
                "describe_application" => JsonSerializer.Serialize(await operations.DescribeApplicationAsync(actor, input.Scope, ct).ConfigureAwait(false), AdminJsonContext.Default.ApplicationTopology),
                "get_system_health" => JsonSerializer.Serialize(await operations.GetSystemHealthAsync(actor, input.Scope, ct).ConfigureAwait(false), AdminJsonContext.Default.OperationalHealth),
                "list_issues" => JsonSerializer.Serialize(await operations.ListIssuesAsync(actor, input.Scope, ct).ConfigureAwait(false), AdminJsonContext.Default.IReadOnlyListOperationalProcessorState),
                "inspect_processor" => JsonSerializer.Serialize(await operations.InspectProcessorAsync(actor, Required(input.Target), ct).ConfigureAwait(false), AdminJsonContext.Default.OperationalProcessorState),
                "trace_causality" => JsonSerializer.Serialize(await operations.TraceCausalityAsync(actor, Required(input.Target), input.AttemptId ?? throw new ArgumentException("Attempt required."), ct).ConfigureAwait(false), AdminJsonContext.Default.OperationalAttempt),
                "preview_repair" => JsonSerializer.Serialize(await operations.PreviewRepairAsync(actor, Required(input.Target), ct).ConfigureAwait(false), AdminJsonContext.Default.RepairPlan),
                "execute_repair" => JsonSerializer.Serialize(await operations.ExecuteRepairAsync(actor, Required(input.Target), input.PlanId ?? throw new ArgumentException("Plan required."), ct).ConfigureAwait(false), AdminJsonContext.Default.OperationalRepair),
                _ => JsonSerializer.Serialize(await operations.VerifyRepairAsync(actor, Required(input.Target), input.PlanId ?? throw new ArgumentException("Plan required."), ct).ConfigureAwait(false), AdminJsonContext.Default.OperationalRepair)
            };
            return new CallToolResult { IsError = false, Content = [new TextContentBlock { Text = json }] };
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or UnauthorizedAccessException
            or InvalidOperationException or KeyNotFoundException)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock
                { Text = "Operational request denied or stale. Supply an authorized exact scope and current recorded identifiers." }]
            };
        }

        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock
                { Text = "Operational evidence is temporarily unavailable." }]
            };
        }

        static OperationalTarget Required(OperationalTarget? target) => target ?? throw new ArgumentException("Target required.");
    }

    sealed class AdminSurface;
    sealed class AdminBodyLimit : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => 64 * 1024;
    }
}

sealed record AdminArguments(OperationalScope Scope, OperationalTarget? Target = null, Guid? AttemptId = null, Guid? PlanId = null);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AdminArguments))]
[JsonSerializable(typeof(IDictionary<string, JsonElement>))]
[JsonSerializable(typeof(OperationalTarget))]
[JsonSerializable(typeof(RepairApproval))]
[JsonSerializable(typeof(ApplicationTopology))]
[JsonSerializable(typeof(OperationalHealth))]
[JsonSerializable(typeof(IReadOnlyList<OperationalProcessorState>))]
[JsonSerializable(typeof(OperationalAttempt))]
[JsonSerializable(typeof(RepairPlan))]
[JsonSerializable(typeof(OperationalRepair))]
sealed partial class AdminJsonContext : JsonSerializerContext;

using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cntryl.Portia.Tests;

[Collection("MCP HTTP integration")]
public sealed class AdminMcpTests
{
    [Fact]
    public async Task ShouldApproveExactPlanOnlyThroughSeparatelyAuthorizedHttpActor()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var store = new ApprovalStore();
        builder.Services.AddSingleton<IPortiaOperations>(new PortiaOperations(store, new OperationalOptions(),
            new EmptyTopology(), new ApprovalPolicy()));
        builder.Services.AddPortia().AddAdminMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            var actor = context.Request.Headers["X-Test-Actor"] == "operator" ? "operator" : "agent";
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor),
                new Claim(ClaimTypes.Role, actor)], "test"));
            return next(context);
        });
        app.MapPortiaAdminMcp().AllowAnonymous();
        app.MapPortiaRepairApprovals().AllowAnonymous();
        await app.StartAsync();
        await using var agent = await ClientAsync(app, "/admin/mcp");
        var arguments = new Dictionary<string, object?> { ["scope"] = new Dictionary<string, object?> { ["tenant"] = null }, ["target"] = store.State.Target };
        var preview = await agent.CallToolAsync("preview_repair", arguments);
        Assert.False(preview.IsError);
        using var plan = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(preview.Content)).Text);
        var id = plan.RootElement.GetProperty("id").GetGuid();
        var http = app.GetTestClient();
        using (var denied = await http.PostAsJsonAsync($"/admin/repairs/{id}/approve", store.State.Target))
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(store.State.Approvals);
        http.DefaultRequestHeaders.Add("X-Test-Actor", "operator");
        using (var approved = await http.PostAsJsonAsync($"/admin/repairs/{id}/approve", store.State.Target))
            Assert.Equal(System.Net.HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("operator", Assert.Single(store.State.Approvals).Actor.Subject);
        arguments["planId"] = id;
        var execution = await agent.CallToolAsync("execute_repair", arguments);
        Assert.False(execution.IsError);
        Assert.Equal("queued", Assert.Single(store.State.Repairs).Status);
        Assert.True((await agent.CallToolAsync("approve_repair", arguments)).IsError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldIsolateCatalogsAndGuessedCallsInEitherRegistrationOrder(bool adminFirst)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var application = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddMcpTool<McpRegistrationTests.ReadGreeting>()
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://app/{name}",
                values => new McpRegistrationTests.ReadGreeting(values["name"]),
                options => { options.Authenticated(); options.AsText("text/plain", value => value); })
            .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("app_prompt",
                values => new McpRegistrationTests.ReadGreeting(values["name"]),
                value => [new McpPromptMessage("user", value)],
                options => { options.Authenticated(); options.Required("name"); });
        builder.Services.AddSingleton<IPortiaOperations>(new PortiaOperations(new EmptyStore(), new OperationalOptions(),
            new EmptyTopology(), new ReadAuthorizer()));
        if (adminFirst)
            application.AddAdminMcpHttp().AddMcpHttp();
        else
            application.AddMcpHttp().AddAdminMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "agent")], "test"));
            return next(context);
        });
        app.MapPortiaMcp().AllowAnonymous();
        app.MapPortiaAdminMcp().AllowAnonymous();
        app.MapPortiaRepairApprovals().AllowAnonymous();
        await app.StartAsync();
        await using var admin = await ClientAsync(app, "/admin/mcp");
        await using var business = await ClientAsync(app, "/mcp");
        var adminTools = await admin.ListToolsAsync();
        Assert.Equal(8, adminTools.Count);
        Assert.DoesNotContain(adminTools, t => t.Name.Contains("approve", StringComparison.Ordinal));
        var applicationTool = Assert.Single(await business.ListToolsAsync());
        Assert.DoesNotContain(adminTools, t => t.Name == applicationTool.Name);
        Assert.True((await admin.CallToolAsync(applicationTool.Name,
            new Dictionary<string, object?> { ["name"] = "Portia" })).IsError);
        _ = await Assert.ThrowsAsync<McpProtocolException>(async () => await business.CallToolAsync("describe_application"));
        _ = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(async () => await admin.ReadResourceAsync("greetings://app/Portia"));
        _ = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(async () => await admin.GetPromptAsync("app_prompt"));
        var missingScope = await admin.CallToolAsync("list_issues");
        Assert.True(missingScope.IsError);
        var tenant = await admin.CallToolAsync("list_issues", new Dictionary<string, object?>
        { ["scope"] = new Dictionary<string, object?> { ["tenant"] = "forbidden" } });
        Assert.True(tenant.IsError);
        var global = await admin.CallToolAsync("list_issues", new Dictionary<string, object?>
        { ["scope"] = new Dictionary<string, object?> { ["tenant"] = null } });
        Assert.False(global.IsError);
    }

    [Fact]
    public void ShouldRejectSharedStatefulHttpConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPortia().AddAdminMcpHttp();
        services.Configure<HttpServerTransportOptions>(options => options.Stateless = false);
        using var provider = services.BuildServiceProvider();
        _ = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HttpServerTransportOptions>>().Value);
    }

    [Fact]
    public async Task ShouldExcludeApplicationFallbackHandlersAndRawMethods()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var calls = 0;
        builder.Services.AddPortia().AddAdminMcpHttp();
        builder.Services.AddSingleton<IPortiaOperations>(new PortiaOperations(new EmptyStore(), new OperationalOptions(),
            new EmptyTopology(), new ReadAuthorizer()));
        builder.Services.Configure<McpServerOptions>(options =>
        {
            options.Handlers.CallToolHandler = (context, ct) =>
            {
                calls++;
                return ValueTask.FromResult(new CallToolResult());
            };
            options.Handlers.ReadResourceHandler = (context, ct) =>
            {
                calls++;
                return ValueTask.FromResult(new ReadResourceResult());
            };
            options.Handlers.GetPromptHandler = (context, ct) =>
            {
                calls++;
                return ValueTask.FromResult(new GetPromptResult());
            };
        });
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity("test"));
            return next(context);
        });
        app.MapPortiaAdminMcp().AllowAnonymous();
        await app.StartAsync();
        await using var admin = await ClientAsync(app, "/admin/mcp");
        Assert.True((await admin.CallToolAsync("application_fallback")).IsError);
        _ = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(async () => await admin.ReadResourceAsync("app://fallback"));
        _ = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(async () => await admin.GetPromptAsync("application_prompt"));
        Assert.Equal(0, calls);
    }

    static Task<McpClient> ClientAsync(WebApplication app, string path) => McpClient.CreateAsync(new HttpClientTransport(
        new HttpClientTransportOptions { Endpoint = new Uri("http://localhost" + path) }, app.GetTestClient(), null));

    sealed class EmptyTopology : IApplicationTopology
    {
        public ApplicationTopology Describe() => new([], [], "unknown opaque dependencies");
    }
    sealed class ReadAuthorizer : IOperationalAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal actor, OperationalScope scope, OperationalAccess permission,
            CancellationToken ct = default) => ValueTask.FromResult(scope == OperationalScope.Global && permission == OperationalAccess.Read);
    }
    sealed class EmptyStore : IOperationalStore
    {
        public ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default) => ValueTask.FromResult<OperationalProcessorState?>(null);
        public ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<OperationalProcessorState>>([]);
        public ValueTask<bool> CompareExchangeAsync(OperationalProcessorState replacement, long expectedRevision, Guid expectedOwner,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    sealed class ApprovalPolicy : IOperationalAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal actor, OperationalScope scope, OperationalAccess permission,
            CancellationToken ct = default) => ValueTask.FromResult(scope == OperationalScope.Global
                && (permission != OperationalAccess.Approve || actor.IsInRole("operator")));
    }

    sealed class ApprovalStore : IOperationalStore
    {
        internal OperationalProcessorState State { get; private set; } = Initial();
        static OperationalProcessorState Initial()
        {
            var owner = Guid.NewGuid();
            var attempt = new OperationalAttempt(Guid.NewGuid(), owner, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                "failed", null, null, [], [], "unknown", new ApplicationTopology([], [], "unknown"));
            return new OperationalProcessorState(new OperationalTarget("processor", "stream://app/events/*", OperationalScope.Global),
                "reactor", owner, 1, null, new OperationalProcessingSettings(512, 4096, 3, 1, 1), [attempt],
                new OperationalBlock(attempt.Id, 1), [], [], [], 1, DateTimeOffset.UtcNow);
        }
        public ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default)
            => ValueTask.FromResult<OperationalProcessorState?>(target == State.Target ? State : null);
        public ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<OperationalProcessorState>>(scope == State.Target.Scope ? [State] : []);
        public ValueTask<bool> CompareExchangeAsync(OperationalProcessorState replacement, long expectedRevision, Guid expectedOwner,
            CancellationToken ct = default)
        {
            if (State.Revision != expectedRevision || State.OwnerSession != expectedOwner)
                return ValueTask.FromResult(false);
            State = replacement;
            return ValueTask.FromResult(true);
        }
    }
}

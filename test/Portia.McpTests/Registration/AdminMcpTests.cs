using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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
    const string AdministratorRole = "platform-operators";

    [Theory]
    [InlineData("initialize")]
    [InlineData("tools/list")]
    public async Task ShouldDenyAdministrativeDiscoveryGivenApplicationUser(string method)
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["PORTIA_SYSTEM_ADMIN_ROLE"] = AdministratorRole;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["PORTIA_SYSTEM_ADMIN_ROLE"] = "platform-operators" });
        builder.Services.AddSingleton<IPortiaOperations>(PortiaOperations.Create(builder.Configuration, new EmptyStore(), new OperationalOptions(),
            new EmptyTopology(), new AllowAllPolicy(), null));
        builder.Services.AddPortia().AddAdminMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "application-user"), new Claim(ClaimTypes.Role, "application-user")], "test"));
            return next(context);
        });
        app.MapPortiaAdminMcp().AllowAnonymous();
        await app.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method +
                "\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}",
                System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        // Act
        using var response = await app.GetTestClient().SendAsync(request);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ShouldDenyRepairApprovalBeforeReadingBodyGivenApplicationUser()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["PORTIA_SYSTEM_ADMIN_ROLE"] = AdministratorRole;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["PORTIA_SYSTEM_ADMIN_ROLE"] = "platform-operators" });
        var store = new ApprovalStore();
        builder.Services.AddSingleton<IPortiaOperations>(PortiaOperations.Create(builder.Configuration, store, new OperationalOptions(),
            new EmptyTopology(), new AllowAllPolicy(), null));
        builder.Services.AddPortia().AddAdminMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "application-user"), new Claim(ClaimTypes.Role, "application-user")], "test"));
            return next(context);
        });
        app.MapPortiaRepairApprovals().AllowAnonymous();
        await app.StartAsync();
        using var content = new StringContent("invalid-json", System.Text.Encoding.UTF8, "application/json");

        // Act
        using var response = await app.GetTestClient().PostAsync($"/admin/repairs/{Guid.NewGuid()}/approve", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(store.State.Approvals);
    }

    [Fact]
    public async Task ShouldApproveExactPlanGivenSeparatelyAuthorizedHttpActor()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["PORTIA_SYSTEM_ADMIN_ROLE"] = AdministratorRole;
        var store = new ApprovalStore();
        builder.Services.AddSingleton<IPortiaOperations>(PortiaOperations.Create(builder.Configuration, store, new OperationalOptions(),
            new EmptyTopology(), new ApprovalPolicy(), null));
        builder.Services.AddPortia().AddAdminMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            var actor = context.Request.Headers["X-Test-Actor"] == "operator" ? "operator" : "agent";
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor),
                new Claim(ClaimTypes.Role, actor), new Claim(ClaimTypes.Role, AdministratorRole)], "test"));
            return next(context);
        });
        app.MapPortiaAdminMcp().AllowAnonymous();
        app.MapPortiaRepairApprovals().AllowAnonymous();
        await app.StartAsync();
        await using var agent = await ClientAsync(app, "/admin/mcp");
        var arguments = new Dictionary<string, object?> { ["scope"] = new Dictionary<string, object?> { ["tenant"] = null }, ["target"] = store.State.Target };

        // Act
        var preview = await agent.CallToolAsync("preview_repair", arguments);

        // Assert
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
    public async Task ShouldIsolateCatalogsAndGuessedCallsGivenEitherRegistrationOrder(bool adminFirst)
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["PORTIA_SYSTEM_ADMIN_ROLE"] = AdministratorRole;
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
        builder.Services.AddSingleton<IPortiaOperations>(PortiaOperations.Create(builder.Configuration, new EmptyStore(), new OperationalOptions(),
            new EmptyTopology(), new ReadAuthorizer(), null));
        if (adminFirst)
            application.AddAdminMcpHttp().AddMcpHttp();
        else
            application.AddMcpHttp().AddAdminMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "agent"),
                new Claim(ClaimTypes.Role, AdministratorRole)], "test"));
            return next(context);
        });
        app.MapPortiaMcp().AllowAnonymous();
        app.MapPortiaAdminMcp().AllowAnonymous();
        app.MapPortiaRepairApprovals().AllowAnonymous();
        await app.StartAsync();
        await using var admin = await ClientAsync(app, "/admin/mcp");
        await using var business = await ClientAsync(app, "/mcp");

        // Act
        var adminTools = await admin.ListToolsAsync();

        // Assert
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
    public void ShouldRejectConfigurationGivenSharedStatefulHttpTransport()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPortia().AddAdminMcpHttp();
        services.Configure<HttpServerTransportOptions>(options => options.Stateless = false);

        // Act
        using var provider = services.BuildServiceProvider();

        // Assert
        _ = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HttpServerTransportOptions>>().Value);
    }

    [Fact]
    public async Task ShouldExcludeFallbackHandlersAndRawMethodsGivenApplicationRegistrations()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["PORTIA_SYSTEM_ADMIN_ROLE"] = AdministratorRole;
        var calls = 0;
        builder.Services.AddPortia().AddAdminMcpHttp();
        builder.Services.AddSingleton<IPortiaOperations>(PortiaOperations.Create(builder.Configuration, new EmptyStore(), new OperationalOptions(),
            new EmptyTopology(), new ReadAuthorizer(), null));
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
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, AdministratorRole)], "test"));
            return next(context);
        });
        app.MapPortiaAdminMcp().AllowAnonymous();
        await app.StartAsync();

        // Act
        await using var admin = await ClientAsync(app, "/admin/mcp");

        // Assert
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
    sealed class AllowAllPolicy : IOperationalAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal actor, OperationalScope scope, OperationalAccess permission,
            CancellationToken ct = default) => ValueTask.FromResult(true);
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

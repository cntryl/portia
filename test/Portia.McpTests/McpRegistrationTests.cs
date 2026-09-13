using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Cntryl.Portia.Tests;

public sealed class McpRegistrationTests
{
    [Fact]
    public void ShouldAddToolToExistingApplicationBuilder()
    {
        // Arrange
        var services = new ServiceCollection();
        var application = services.AddPortia().AddRequestHandler<ReadGreetingHandler>();

        // Act
        var returned = application.AddMcpTool<ReadGreeting>(tool => tool.ReadOnly().Titled("Read greeting"));

        // Assert
        Assert.Same(application, returned);
        using var provider = services.BuildServiceProvider();
        var tool = Assert.Single(provider.GetServices<McpServerTool>());
        Assert.Equal("greetings.read", tool.ProtocolTool.Name);
        Assert.Equal("Reads a greeting without changing application state.", tool.ProtocolTool.Description);
        Assert.Equal("Read greeting", tool.ProtocolTool.Title);
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task ShouldMapHttpFromSharedToolDeclarations()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>();

        // Act
        await using var app = builder.Build();
        _ = app.MapPortiaMcp();
        await app.StartAsync();

        // Assert
        Assert.Contains(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints), endpoint =>
            endpoint.DisplayName?.Contains("MCP", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task ShouldDiscoverAndInvokeToolOverStreamableHttp()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>(tool => tool.ReadOnly());
        await using var app = builder.Build();
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), NullLoggerFactory.Instance, false);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        var tools = await client.ListToolsAsync();
        var result = await client.CallToolAsync("greetings.read", new Dictionary<string, object?>
        {
            ["name"] = "Portia"
        }!);

        // Assert
        Assert.Contains(tools, tool => tool.Name == "greetings.read");
        Assert.False(result.IsError);
        Assert.Equal("\"Hello, Portia.\"", result.StructuredContent?.GetRawText());
    }

    [Fact]
    public void ShouldRejectConflictingDeclarations()
    {
        // Arrange
        var application = new ServiceCollection().AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            application.AddMcpTool<ReadGreeting>(tool => tool.Named("greetings.other")));

        // Assert
        Assert.Contains("conflicting MCP tool declarations", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRejectEmptyTransportActivation()
    {
        var stdio = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPortia().AddMcpStdio());
        Assert.Contains("at least one AddMcpTool", stdio.Message, StringComparison.Ordinal);

        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        var http = Assert.Throws<InvalidOperationException>(() => app.MapPortiaMcp());
        Assert.Contains("at least one AddMcpTool", http.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldRejectExposedRequestWithoutHandlerBeforeServing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddMcpTool<ReadGreeting>();
        await using var app = builder.Build();
        _ = app.MapPortiaMcp();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("greetings.read", error.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(ReadGreeting).ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldMapExpectedBusinessFailureAsStructuredToolResult()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<RejectGreetingHandler>()
            .AddMcpTool<RejectGreeting>();
        await using var app = builder.Build();
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), NullLoggerFactory.Instance, false);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        var result = await client.CallToolAsync("greetings.reject", new Dictionary<string, object?>());

        // Assert
        Assert.True(result.IsError);
        Assert.Equal("Conflict", result.StructuredContent?.GetProperty("kind").GetString());
        Assert.Equal("Greeting already exists.", result.StructuredContent?.GetProperty("message").GetString());
        Assert.True(result.StructuredContent?.GetProperty("isTransient").GetBoolean());
    }

    [Fact]
    public async Task ShouldUseHttpPrincipalAsPortiaActor()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadActorHandler>()
            .AddMcpTool<ReadActor>();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "authenticated-agent")], "test"));
            return next(context);
        });
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), NullLoggerFactory.Instance, false);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        var result = await client.CallToolAsync("actors.read", new Dictionary<string, object?>());

        // Assert
        Assert.Equal("\"authenticated-agent|Mcp|actors.read\"", result.StructuredContent?.GetRawText());
    }

    [Fact]
    public async Task ShouldComposeWithAspNetAuthorization()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, UnauthenticatedHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>();
        await using var app = builder.Build();
        app.UseAuthorization();
        _ = app.MapPortiaMcp().RequireAuthorization();
        await app.StartAsync();

        // Act
        using var response = await app.GetTestClient().PostAsJsonAsync("/mcp", new { });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ShouldDiscoverAndInvokeToolFromExternalStdioHost()
    {
        // Arrange
        var host = Path.GetFullPath(
            "../../../../../smoke/Portia.McpStdioHost/bin/Release/net10.0/Portia.McpStdioHost.dll",
            AppContext.BaseDirectory);
        Assert.True(File.Exists(host), $"The stdio test host was not built: {host}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [host],
            Name = "Portia stdio integration test",
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        }, NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport,
            cancellationToken: timeout.Token);

        // Act
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        var result = await client.CallToolAsync("stdio.greetings.read", new Dictionary<string, object?>
        {
            ["name"] = "Portia"
        }!, cancellationToken: timeout.Token);

        // Assert
        Assert.Contains(tools, tool => tool.Name == "stdio.greetings.read");
        Assert.Equal("\"Hello, Portia, from stdio-test-actor.\"", result.StructuredContent?.GetRawText());
    }

    [Fact]
    public async Task ShouldPropagateClientCancellationToHandler()
    {
        // Arrange
        var probe = new CancellationProbe();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(probe);
        _ = builder.Services.AddPortia()
            .AddRequestHandler<WaitForCancellationHandler>()
            .AddMcpTool<WaitForCancellation>();
        await using var app = builder.Build();
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), NullLoggerFactory.Instance, false);
        await using var client = await McpClient.CreateAsync(transport);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        var call = client.CallToolAsync("cancellation.wait", new Dictionary<string, object?>(),
            cancellationToken: cancellation.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        // Assert
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
        await probe.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShouldCreateFreshBoundedReceiveAndExecuteActivities()
    {
        // Arrange
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>();
        await using var app = builder.Build();
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), NullLoggerFactory.Instance, false);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        _ = await client.CallToolAsync("greetings.read", new Dictionary<string, object?> { ["name"] = "trace" });

        // Assert
        var receive = Assert.Single(stopped, activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        var execute = Assert.Single(stopped, activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName);
        Assert.Equal(receive.Id, execute.ParentId);
        Assert.Equal("Mcp", receive.GetTagItem("portia.transport.name"));
        Assert.Equal("Mcp", execute.GetTagItem("portia.transport.name"));
        Assert.DoesNotContain(receive.TagObjects, tag => tag.Key.Contains("argument", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads a greeting without changing application state.</summary>
    [Discriminator("greetings.read")]
    [RequestRoute("public", "greetings", "message", "read")]
    public sealed record ReadGreeting(string Name) : IRequest<string>, ICallable;

    public sealed class ReadGreetingHandler : IRequestHandler<ReadGreeting, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<ReadGreeting> context, CancellationToken ct) =>
            ValueTask.FromResult(Result<string>.Success($"Hello, {context.Request.Name}."));
    }

    /// <summary>Always rejects a greeting to freeze the MCP business-error envelope.</summary>
    [Discriminator("greetings.reject")]
    public sealed record RejectGreeting : IRequest, ICallable;

    public sealed class RejectGreetingHandler : IRequestHandler<RejectGreeting>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<RejectGreeting> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Failure(
                new RequestError(RequestErrorKind.Conflict, "Greeting already exists.", true)));
    }

    /// <summary>Returns the authenticated Portia actor name.</summary>
    [Discriminator("actors.read")]
    public sealed record ReadActor : IRequest<string>, ICallable;

    public sealed class ReadActorHandler : IRequestHandler<ReadActor, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<ReadActor> context, CancellationToken ct) =>
            ValueTask.FromResult(Result<string>.Success(
                $"{context.Actor.Identity?.Name ?? "anonymous"}|{context.Invocation.TransportName}|{((McpInvocation)context.Invocation).ToolName}"));
    }

    sealed class UnauthenticatedHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>Waits until the MCP client cancels its invocation.</summary>
    [Discriminator("cancellation.wait")]
    public sealed record WaitForCancellation : IRequest, ICallable;

    public sealed class WaitForCancellationHandler(CancellationProbe probe) : IRequestHandler<WaitForCancellation>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<WaitForCancellation> context,
            CancellationToken ct)
        {
            probe.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Result.Success;
            }
            catch (OperationCanceledException)
            {
                probe.Canceled.TrySetResult();
                throw;
            }
        }
    }

    public sealed class CancellationProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

[PortiaJsonContext]
[JsonSerializable(typeof(McpRegistrationTests.ReadGreeting))]
[JsonSerializable(typeof(McpRegistrationTests.RejectGreeting))]
[JsonSerializable(typeof(McpRegistrationTests.ReadActor))]
[JsonSerializable(typeof(McpRegistrationTests.WaitForCancellation))]
[JsonSerializable(typeof(string))]
public sealed partial class McpTestsJsonContext : JsonSerializerContext;

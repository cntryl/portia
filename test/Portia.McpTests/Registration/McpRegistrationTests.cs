using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Portia.McpContracts;
using Cntryl.Portia.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Cntryl.Portia.Tests;

[Collection("MCP HTTP integration")]
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
        Assert.Equal(JsonValueKind.Object, tool.ProtocolTool.OutputSchema?.ValueKind);
        Assert.Equal("object", tool.ProtocolTool.OutputSchema?.GetProperty("type").GetString());
        Assert.Equal("string", tool.ProtocolTool.OutputSchema?.GetProperty("properties")
            .GetProperty("result").GetProperty("type")[0].GetString());
        Assert.Single(provider.GetServices<McpToolRegistration>());
    }

    [Fact]
    public void ShouldRenderXmlReferencesAndResolveInheritedDocumentation()
    {
        var services = new ServiceCollection();
        _ = services.AddPortia()
            .AddRequestHandler<MarkupGreetingHandler>()
            .AddRequestHandler<InheritedGreetingHandler>()
            .AddMcpTool<MarkupGreeting>()
            .AddMcpTool<InheritedGreeting>();

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetServices<McpServerTool>().ToDictionary(tool => tool.ProtocolTool.Name);

        Assert.Equal("Returns ReadGreeting when the null value is missing.",
            tools["greetings.markup"].ProtocolTool.Description);
        Assert.Equal("Reads a greeting inherited by the concrete MCP request.",
            tools["greetings.inherited"].ProtocolTool.Description);
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("not a tool")]
    [InlineData("tøøl")]
    public void ShouldRejectInvalidMcpToolNames(string name)
    {
        var application = new ServiceCollection().AddPortia()
            .AddRequestHandler<ReadGreetingHandler>();

        var exception = Assert.Throws<ArgumentException>(() =>
            application.AddMcpTool<ReadGreeting>(tool => tool.Named(name)));

        Assert.Contains("1 to 128 ASCII", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRejectMcpToolNamesLongerThan128Characters()
    {
        var application = new ServiceCollection().AddPortia()
            .AddRequestHandler<ReadGreetingHandler>();

        _ = Assert.Throws<ArgumentException>(() =>
            application.AddMcpTool<ReadGreeting>(tool => tool.Named(new string('a', 129))));
    }

    [Fact]
    public void ShouldRejectInvalidDiscriminatorAsMcpToolName()
    {
        var application = new ServiceCollection().AddPortia()
            .AddRequestHandler<InvalidNameGreetingHandler>();

        _ = Assert.Throws<ArgumentException>(() => application.AddMcpTool<InvalidNameGreeting>());
    }

    [Fact]
    public void ShouldRewriteResultSchemaReferencesInsideObjectEnvelope()
    {
        var services = new ServiceCollection();
        _ = services.AddPortia()
            .AddRequestHandler<ReadTreeHandler>()
            .AddMcpTool<ReadTree>();

        using var provider = services.BuildServiceProvider();
        var schema = Assert.Single(provider.GetServices<McpServerTool>()).ProtocolTool.OutputSchema?.GetRawText();

        Assert.Contains("\"$ref\":\"#/properties/result/", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$ref\":\"#/properties/child", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldGenerateDescriptionForRequestFromContractsAssemblyWithoutXmlFile()
    {
        var services = new ServiceCollection();
        _ = services.AddPortia()
            .AddRequestHandler<ExternalGreetingHandler>()
            .AddMcpTool<ExternalGreeting>(tool => tool.ReadOnly());

        using var provider = services.BuildServiceProvider();
        var tool = Assert.Single(provider.GetServices<McpServerTool>());

        Assert.Equal("Invokes the ExternalGreeting request.", tool.ProtocolTool.Description);
    }

    [Fact]
    public async Task ShouldMapHttpFromSharedToolDeclarations()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>()
            .AddMcpHttp();

        // Act
        await using var app = builder.Build();
        UseTestActor(app);
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
            .AddMcpTool<ReadGreeting>(tool => tool.ReadOnly())
            .AddMcpHttp();
        await using var app = builder.Build();
        UseTestActor(app);
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        var tools = await client.ListToolsAsync();
        var result = await client.CallToolAsync("greetings.read", new Dictionary<string, object?>
        {
            ["name"] = "Portia"
        }!);

        // Assert
        Assert.Contains(tools, tool => tool.Name == "greetings.read");
        Assert.False(result.IsError, result.StructuredContent?.GetRawText());
        Assert.Equal("Hello, Portia.", result.StructuredContent?.GetProperty("result").GetString());
    }

    /// <summary>
    ///     Streamable HTTP must validate the browser origin; the MCP endpoint applies Portia's cross-origin
    ///     rule, trusting exactly the origins the application's CORS pipeline allows.
    /// </summary>
    [Fact]
    public async Task ShouldRejectCrossOriginMcpRequestsUnlessCorsAllowsTheOrigin()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddCors(options =>
            options.AddPolicy("agents", policy => policy.WithOrigins("https://agent.example")));
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>(tool => tool.ReadOnly())
            .AddMcpHttp();
        await using var app = builder.Build();
        UseTestActor(app);
        _ = app.UseCors("agents");
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        // Act
        using var rejected = await client.SendAsync(CrossOriginListTools("https://attacker.example"));
        using var allowed = await client.SendAsync(CrossOriginListTools("https://agent.example"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    static HttpRequestMessage CrossOriginListTools(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent( /*lang=json,strict*/ """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Origin", origin);
        return request;
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

        var httpServices = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPortia().AddMcpHttp());
        Assert.Contains("at least one AddMcpTool", httpServices.Message, StringComparison.Ordinal);

        var builder = WebApplication.CreateBuilder();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>();
        using var app = builder.Build();
        var http = Assert.Throws<InvalidOperationException>(() => app.MapPortiaMcp());
        Assert.Contains("AddMcpHttp", http.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRequireExplicitStdioActorPolicyAndAllowScopedProviders()
    {
        var missingActor = new ServiceCollection().AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>();
        var exception = Assert.Throws<InvalidOperationException>(() => missingActor.AddMcpStdio());
        Assert.Contains("explicit actor policy", exception.Message, StringComparison.Ordinal);

        var services = new ServiceCollection();
        _ = services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>()
            .AddMcpStdio(options => options.UseActorProvider<ScopedActorProvider>());
        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IMcpActorProvider));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void ShouldApplyPortiaHttpRequestBodyLimitToMcpEndpoint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = 1_234);
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>()
            .AddMcpHttp();
        using var app = builder.Build();
        _ = app.MapPortiaMcp();

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints),
            candidate => candidate.DisplayName?.Contains("MCP", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(1_234, endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
        Assert.Null(app.Services.GetService<IHttpContextAccessor>());
    }

    [Fact]
    public void ShouldLetExplicitHttpConfigurationOverridePortiaDefaults()
    {
        var services = new ServiceCollection();
        _ = services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>()
            .AddMcpHttp(options => options.Stateless = false);

        using var provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value.Stateless);
    }

    [Fact]
    public async Task ShouldRejectExposedRequestWithoutHandlerBeforeServing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddMcpTool<ReadGreeting>().AddMcpHttp();
        await using var app = builder.Build();
        UseTestActor(app);
        _ = app.MapPortiaMcp();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("greetings.read", error.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(ReadGreeting).ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldMapExpectedBusinessFailureAsStructuredToolResult()
    {
        // Arrange
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<RejectGreetingHandler>()
            .AddMcpTool<RejectGreeting>()
            .AddMcpHttp();
        await using var app = builder.Build();
        UseTestActor(app);
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        var result = await client.CallToolAsync("greetings.reject", new Dictionary<string, object?>());

        // Assert
        Assert.True(result.IsError);
        Assert.Equal("Conflict", result.StructuredContent?.GetProperty("kind").GetString());
        Assert.Equal("Greeting already exists.", result.StructuredContent?.GetProperty("message").GetString());
        Assert.True(result.StructuredContent?.GetProperty("isTransient").GetBoolean());
        var receive = Assert.Single(stopped, activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        var execute = Assert.Single(stopped, activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName);
        Assert.Equal("conflict", receive.GetTagItem("portia.outcome"));
        Assert.Equal("conflict", execute.GetTagItem("portia.outcome"));
    }

    [Fact]
    public async Task ShouldReportPostDispatchJsonExceptionAsInternalAndRecordTheFault()
    {
        var probe = new MutationProbe();
        var logs = new CapturingLoggerProvider();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(probe);
        _ = builder.Services.AddPortia()
            .AddRequestHandler<PostDispatchJsonFailureHandler>()
            .AddMcpTool<PostDispatchJsonFailure>()
            .AddMcpHttp();
        await using var app = builder.Build();
        UseTestActor(app);
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var client = await HttpClientAsync(app);

        var result = await client.CallToolAsync("failures.json", new Dictionary<string, object?>());

        Assert.Equal(1, probe.Committed);
        Assert.True(result.IsError);
        Assert.Equal("Internal", result.StructuredContent?.GetProperty("kind").GetString());
        Assert.Equal("The tool could not be completed.",
            result.StructuredContent?.GetProperty("message").GetString());
        Assert.DoesNotContain("committed", result.StructuredContent?.GetRawText(), StringComparison.Ordinal);
        var receive = Assert.Single(stopped, activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        Assert.Equal(ActivityStatusCode.Error, receive.Status);
        Assert.Equal("fault", receive.GetTagItem("portia.outcome"));
        Assert.Contains(receive.Events, activityEvent => activityEvent.Name == "exception");
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Error
                                               && entry.Exception is JsonException);
    }

    [Fact]
    public async Task ShouldNotClassifyUnrelatedActorExceptionAsUnauthorized()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ArchivedActorFailureHandler>()
            .AddMcpTool<ArchivedActorFailure>()
            .AddMcpHttp();
        await using var app = builder.Build();
        UseTestActor(app);
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var client = await HttpClientAsync(app);

        var result = await client.CallToolAsync("failures.actor", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        Assert.Equal("Internal", result.StructuredContent?.GetProperty("kind").GetString());
        Assert.DoesNotContain("Actor 42", result.StructuredContent?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldRecordInvalidToolArgumentsAsValidation()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>()
            .AddMcpHttp();
        await using var app = builder.Build();
        UseTestActor(app);
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var client = await HttpClientAsync(app);

        var result = await client.CallToolAsync("greetings.read", new Dictionary<string, object?>
        {
            ["name"] = 42
        });

        Assert.True(result.IsError);
        Assert.Equal("Binding", result.StructuredContent?.GetProperty("kind").GetString());
        var receive = Assert.Single(stopped, activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        Assert.Equal("validation", receive.GetTagItem("portia.outcome"));
        Assert.Equal(ActivityStatusCode.Error, receive.Status);
    }

    [Fact]
    public async Task ShouldUseHttpPrincipalAsPortiaActor()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadActorHandler>()
            .AddMcpTool<ReadActor>()
            .AddMcpHttp();
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
        }, app.GetTestClient(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        var result = await client.CallToolAsync("actors.read", new Dictionary<string, object?>());

        // Assert
        Assert.Equal("authenticated-agent|mcp|actors.read",
            result.StructuredContent?.GetProperty("result").GetString());
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
            .AddMcpTool<ReadGreeting>()
            .AddMcpHttp();
        await using var app = builder.Build();
        app.UseAuthorization();
        _ = app.MapPortiaMcp().RequireAuthorization();
        await app.StartAsync();

        // Act
        using var http = app.GetTestClient();
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await McpScenario.ConnectAsync(http, new Uri("http://localhost/mcp")));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task ShouldDiscoverAndInvokeToolFromExternalStdioHost()
    {
        // Arrange
        var host = StdioHostPath();
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
        Assert.Equal("Hello, Portia, from stdio-test-actor.",
            result.StructuredContent?.GetProperty("result").GetString());
    }

    [Fact]
    public async Task ShouldKeepStdioProtocolStdoutFreeOfLogLines()
    {
        var host = StdioHostPath();
        Assert.True(File.Exists(host), $"The stdio test host was not built: {host}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add(host);
        Assert.True(process.Start());
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"strict-test\",\"version\":\"1\"}}}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var response = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(response), "The stdio host returned no initialize response.");
            using (JsonDocument.Parse(response))
            {
            }

            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}");
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var toolsResponse = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(toolsResponse), "The stdio host returned no tools response.");
            using (var toolsDocument = JsonDocument.Parse(toolsResponse))
            {
                var tool = Assert.Single(toolsDocument.RootElement.GetProperty("result").GetProperty("tools")
                    .EnumerateArray().ToArray());
                Assert.Equal("object", tool.GetProperty("outputSchema").GetProperty("type").GetString());
            }

            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"stdio.greetings.read\",\"arguments\":{\"name\":\"legacy\"}}}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var callResponse = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(callResponse), "The stdio host returned no call response.");
            using (var callDocument = JsonDocument.Parse(callResponse))
            {
                Assert.Equal("Hello, legacy, from stdio-test-actor.", callDocument.RootElement
                    .GetProperty("result").GetProperty("structuredContent").GetProperty("result").GetString());
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var remainder = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            foreach (var line in remainder.Split('\n', StringSplitOptions.RemoveEmptyEntries
                                                       | StringSplitOptions.TrimEntries))
            {
                using (JsonDocument.Parse(line))
                {
                }
            }
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(true);
        }
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
            .AddMcpTool<WaitForCancellation>()
            .AddMcpHttp();
        await using var app = builder.Build();
        UseTestActor(app);
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), NullLoggerFactory.Instance);
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
            Sample = static (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadGreetingHandler>()
            .AddMcpTool<ReadGreeting>()
            .AddMcpHttp();
        await using var app = builder.Build();
        UseTestActor(app);
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        _ = await client.CallToolAsync("greetings.read", new Dictionary<string, object?> { ["name"] = "trace" });

        // Assert
        var receive = Assert.Single(stopped, activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        var execute = Assert.Single(stopped, activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName);
        Assert.Equal(receive.Id, execute.ParentId);
        Assert.Equal("mcp", receive.GetTagItem("portia.transport.name"));
        Assert.Equal("mcp", execute.GetTagItem("portia.transport.name"));
        Assert.DoesNotContain(receive.TagObjects,
            tag => tag.Key.Contains("argument", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ShouldKeepTelemetryKeyedByDiscriminatorWhenToolIsRenamed()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadActorHandler>()
            .AddMcpTool<ReadActor>(tool => tool.Named("actors.alias"))
            .AddMcpHttp();
        await using var app = builder.Build();
        UseTestActor(app, "aliased-agent");
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        await using var client = await HttpClientAsync(app);

        var result = await client.CallToolAsync("actors.alias", new Dictionary<string, object?>());

        Assert.Equal("aliased-agent|mcp|actors.alias",
            result.StructuredContent?.GetProperty("result").GetString());
        var receive = Assert.Single(stopped, activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        var execute = Assert.Single(stopped, activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName);
        Assert.Equal("actors.read", receive.GetTagItem("portia.request.name"));
        Assert.Equal("actors.read", execute.GetTagItem("portia.request.name"));
    }

    static string StdioHostPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                            ?? throw new InvalidOperationException("The test configuration could not be determined.");
        return Path.GetFullPath(
            $"../../../../../smoke/Portia.McpStdioHost/bin/{configuration}/net10.0/Portia.McpStdioHost.dll",
            AppContext.BaseDirectory);
    }

    static ActivityListener Listen(ConcurrentQueue<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    static async ValueTask<McpClient> HttpClientAsync(WebApplication app)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), NullLoggerFactory.Instance);
        return await McpClient.CreateAsync(transport);
    }

    static void UseTestActor(WebApplication app, string name = "mcp-test-actor") =>
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, name)], "test"));
            return next(context);
        });

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
    [RequestRoute("public", "actors", "actor", "read")]
    public sealed record ReadActor : IRequest<string>, ICallable;

    public sealed class ReadActorHandler : IRequestHandler<ReadActor, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<ReadActor> context, CancellationToken ct) =>
            ValueTask.FromResult(Result<string>.Success(
                $"{context.Actor.Identity?.Name ?? "anonymous"}|{context.Invocation.TransportName}|{((McpInvocation)context.Invocation).ToolName}"));
    }

    sealed class UnauthenticatedHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
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

    public sealed class ExternalGreetingHandler : IRequestHandler<ExternalGreeting, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<ExternalGreeting> context,
            CancellationToken ct) => ValueTask.FromResult(Result<string>.Success(context.Request.Name));
    }

    /// <summary>Returns <see cref="ReadGreeting" /> when the <see langword="null" /> value is missing.</summary>
    [Discriminator("greetings.markup")]
    public sealed record MarkupGreeting(string Name) : IRequest<string>, ICallable;

    public sealed class MarkupGreetingHandler : IRequestHandler<MarkupGreeting, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<MarkupGreeting> context,
            CancellationToken ct) => ValueTask.FromResult(Result<string>.Success(context.Request.Name));
    }

    /// <summary>Reads a greeting inherited by the concrete MCP request.</summary>
    public interface IInheritedGreeting;

    /// <inheritdoc />
    [Discriminator("greetings.inherited")]
    public sealed record InheritedGreeting(string Name) : IRequest<string>, ICallable, IInheritedGreeting;

    public sealed class InheritedGreetingHandler : IRequestHandler<InheritedGreeting, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<InheritedGreeting> context,
            CancellationToken ct) => ValueTask.FromResult(Result<string>.Success(context.Request.Name));
    }

    public sealed class ScopedActorProvider : IMcpActorProvider
    {
        public ValueTask<ClaimsPrincipal> GetActorAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new ClaimsPrincipal(new ClaimsIdentity("test")));
    }

    /// <summary>Commits a probe before throwing a JSON exception.</summary>
    [Discriminator("failures.json")]
    public sealed record PostDispatchJsonFailure : IRequest, ICallable;

    public sealed class PostDispatchJsonFailureHandler(MutationProbe probe) : IRequestHandler<PostDispatchJsonFailure>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<PostDispatchJsonFailure> context, CancellationToken ct)
        {
            probe.Committed++;
            throw new JsonException("committed mutation then failed");
        }
    }

    /// <summary>Throws an operation error that happens to mention an actor.</summary>
    [Discriminator("failures.actor")]
    public sealed record ArchivedActorFailure : IRequest, ICallable;

    public sealed class ArchivedActorFailureHandler : IRequestHandler<ArchivedActorFailure>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<ArchivedActorFailure> context, CancellationToken ct) =>
            throw new InvalidOperationException("Actor 42 is archived");
    }

    public sealed class MutationProbe
    {
        public int Committed { get; set; }
    }

    /// <summary>Has a discriminator that is not legal in the MCP tool grammar.</summary>
    [Discriminator("invalid/name")]
    public sealed record InvalidNameGreeting : IRequest, ICallable;

    public sealed class InvalidNameGreetingHandler : IRequestHandler<InvalidNameGreeting>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<InvalidNameGreeting> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    /// <summary>Returns a recursive result schema.</summary>
    [Discriminator("trees.read")]
    public sealed record ReadTree : IRequest<TreeNode>, ICallable;

    public sealed record TreeNode(string Name, TreeNode? Child);

    public sealed class ReadTreeHandler : IRequestHandler<ReadTree, TreeNode>
    {
        public ValueTask<Result<TreeNode>> HandleAsync(IRequestContext<ReadTree> context, CancellationToken ct) =>
            ValueTask.FromResult(Result<TreeNode>.Success(new TreeNode("root", null)));
    }

    sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        sealed class CapturingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, exception, formatter(state, exception)));
        }
    }

    sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);
}

[PortiaJsonContext]
[JsonSerializable(typeof(McpRegistrationTests.ReadGreeting))]
[JsonSerializable(typeof(McpRegistrationTests.RejectGreeting))]
[JsonSerializable(typeof(McpRegistrationTests.ReadActor))]
[JsonSerializable(typeof(McpRegistrationTests.WaitForCancellation))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ExternalGreeting))]
[JsonSerializable(typeof(McpRegistrationTests.InheritedGreeting))]
[JsonSerializable(typeof(McpRegistrationTests.MarkupGreeting))]
[JsonSerializable(typeof(McpRegistrationTests.PostDispatchJsonFailure))]
[JsonSerializable(typeof(McpRegistrationTests.ArchivedActorFailure))]
[JsonSerializable(typeof(McpRegistrationTests.InvalidNameGreeting))]
[JsonSerializable(typeof(McpRegistrationTests.ReadTree))]
[JsonSerializable(typeof(McpRegistrationTests.TreeNode))]
public sealed partial class McpTestsJsonContext : JsonSerializerContext;

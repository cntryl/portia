using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Cntryl.Portia.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cntryl.Portia.Tests;

[Collection("MCP HTTP integration")]
public sealed class McpScenarioTests
{
    [Fact]
    public async Task ShouldTerminateStdioChildGivenScenarioDisposal()
    {
        // Arrange
        var host = GetStdioHostPath();
        var processId = 0;
        var options = new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [host, "--report-process-id"],
            Name = "Portia stdio disposal test",
            StandardErrorLines = line =>
            {
                if (int.TryParse(line, out var parsed))
                    Interlocked.Exchange(ref processId, parsed);
            }
        };

        // Act
        await using (var scenario = await McpScenario.ConnectStdioAsync(options))
            _ = await scenario.ListTools().ExpectExactly("stdio.greetings.read");

        // Assert
        Assert.True(Volatile.Read(ref processId) > 0, "The child process did not report its PID.");
        Assert.True(await WaitForProcessExitAsync(Volatile.Read(ref processId), TimeSpan.FromSeconds(5)),
            "Disposing the scenario left its stdio child process running.");
    }

    [Fact]
    public async Task ShouldTerminateStdioChildGivenCanceledConnection()
    {
        // Arrange
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var options = new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [GetStdioHostPath(), "--hang-before-mcp"],
            Name = "Portia stdio canceled connection test",
            StandardErrorLines = line =>
            {
                if (int.TryParse(line, out var processId) && started.TrySetResult(processId))
                    cancellation.Cancel();
            }
        };

        // Act
        async Task ConnectAsync()
        {
            await using var scenario = await McpScenario.ConnectStdioAsync(options, cancellation.Token);
        }

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(ConnectAsync);
        var childProcessId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await WaitForProcessExitAsync(childProcessId, TimeSpan.FromSeconds(5)),
            "Canceling stdio connection left its child process running.");
    }

    [Fact]
    public async Task ShouldTerminateStdioChildGivenFailedInitialization()
    {
        // Arrange
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedProcessId = 0;
        var processIdFile = Path.Combine(Path.GetTempPath(), $"portia-mcp-{Guid.NewGuid():N}.pid");
        var options = new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [GetStdioHostPath(), "--fail-initialization"],
            Name = "Portia stdio failed initialization test",
            ShutdownTimeout = TimeSpan.FromMilliseconds(500),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["PORTIA_MCP_TEST_PROCESS_ID_FILE"] = processIdFile
            },
            StandardErrorLines = line =>
            {
                if (int.TryParse(line, out var processId))
                {
                    Interlocked.Exchange(ref observedProcessId, processId);
                    started.TrySetResult(processId);
                }
            }
        };

        async Task ConnectAsync()
        {
            await using var scenario = await McpScenario.ConnectStdioAsync(options);
        }

        // Act
        var childProcessId = 0;

        // Assert
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(ConnectAsync);
            Assert.False(failure is OperationCanceledException,
                "MCP initialization should fail with a protocol error, not cancellation.");
            childProcessId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await WaitForProcessExitAsync(childProcessId, TimeSpan.FromSeconds(5)),
                "A failed MCP initialization left its stdio child process running.");
        }
        finally
        {
            try
            {
                if (childProcessId == 0)
                    childProcessId = Volatile.Read(ref observedProcessId);
                if (childProcessId == 0 && File.Exists(processIdFile)
                    && int.TryParse(await File.ReadAllTextAsync(processIdFile), out var persistedProcessId))
                    childProcessId = persistedProcessId;
                if (childProcessId > 0)
                {
                    KillProcessIfRunning(childProcessId);
                    Assert.True(await WaitForProcessExitAsync(childProcessId, TimeSpan.FromSeconds(5)),
                        "Cleanup after a failed MCP initialization left its stdio child process running.");
                }
            }
            finally
            {
                File.Delete(processIdFile);
            }
        }
    }

    [Fact]
    public void ShouldPreserveConstructionAndDeconstructionGivenPrimitiveSnapshots()
    {
        // Arrange
        var resource = new McpResourceSnapshot("resource://one", "One", "text/plain", false);

        // Act
        var (uri, name, mimeType, isTemplate) = resource;

        // Assert
        Assert.Equal(("resource://one", "One", "text/plain", false), (uri, name, mimeType, isTemplate));

        var contents = new McpResourceContentSnapshot("resource://one", "text/plain", "content", null);
        var (contentUri, contentMimeType, text, bytes) = contents;
        Assert.Equal(("resource://one", "text/plain", "content", null),
            (contentUri, contentMimeType, text, bytes));

        var prompt = new McpPromptSnapshot("prompt", new Dictionary<string, bool> { ["name"] = true });
        var (promptName, arguments) = prompt;
        Assert.Equal("prompt", promptName);
        Assert.True(arguments["name"]);
    }

    [Fact]
    public async Task ShouldExerciseToolsResourcesAndPromptsGivenOwnedStdioScenario()
    {
        // Arrange
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                            ?? throw new InvalidOperationException("Missing test configuration.");
        var host = Path.GetFullPath(
            $"../../../../../smoke/Portia.McpStdioHost/bin/{configuration}/net11.0/Portia.McpStdioHost.dll",
            AppContext.BaseDirectory);

        // Act
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Assert
        await using (var scenario = await McpScenario.ConnectStdioAsync(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [host],
            Name = "Portia stdio scenario test"
        }, timeout.Token))
        {
            _ = await scenario.ListTools().ExpectExactly("stdio.greetings.read");
            _ = await scenario.When("stdio.greetings.read", new Dictionary<string, object?> { ["name"] = "Portia" })
                .ExpectResult(JsonSerializerOptions.Default.GetTypeInfo<string>(), value =>
                    Assert.Equal("Hello, Portia, from stdio-test-actor.", value));
        }

        await using (var scenario = await McpScenario.ConnectStdioAsync(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [host, "--resource-only"],
            Name = "Portia stdio resource test"
        }, timeout.Token))
        {
            _ = await scenario.ListResourceTemplates().ExpectExactly("greetings://stdio/{name}");
            var resource = await scenario.ReadResource("greetings://stdio/Portia");
            Assert.Contains("stdio-test-actor", Assert.Single(resource).Text, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.Zero, Assert.Single(resource).TimeToLive);
            Assert.Equal(CacheScope.Private, Assert.Single(resource).CacheScope);
        }

        await using var promptScenario = await McpScenario.ConnectStdioAsync(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [host, "--prompt-only"],
            Name = "Portia stdio prompt test"
        }, timeout.Token);
        _ = await promptScenario.ListPrompts().ExpectExactly("stdio-greeting");
        var prompt = await promptScenario.GetPrompt("stdio-greeting", new Dictionary<string, object?> { ["name"] = "Portia" });
        Assert.Contains("stdio-test-actor", Assert.Single(prompt).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldSnapshotCatalogAndExerciseDispatchFailuresGivenRealDispatch()
    {
        // Arrange
        var postRequests = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddRequestHandler<McpRegistrationTests.RejectGreetingHandler>()
            .AddRequestHandler<McpRegistrationTests.ReadActorHandler>()
            .AddMcpTool<McpRegistrationTests.ReadGreeting>(tool => tool.ReadOnly().Titled("Read greeting"))
            .AddMcpTool<McpRegistrationTests.RejectGreeting>()
            .AddMcpTool<McpRegistrationTests.ReadActor>()
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://scenario/{name}",
                values => new McpRegistrationTests.ReadGreeting(values["name"]),
                resource => { resource.Authenticated(); resource.AsText("text/plain", value => value); })
            .AddMcpResource<McpRegistrationTests.ReadActor, string>("greetings://local/fixed",
                _ => new McpRegistrationTests.ReadActor(),
                resource =>
                {
                    resource.Authenticated();
                    resource.Named("fixed greeting");
                    resource.AsText("text/plain", value => value);
                })
            .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("scenario-greeting",
                values => new McpRegistrationTests.ReadGreeting(values["name"]),
                value => [new("user", value)], prompt => { prompt.Authenticated(); prompt.Required("name"); })
            .AddMcpHttp();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/mcp" && context.Request.Method == "POST")
                Interlocked.Increment(ref postRequests);
            await next(context);
        });
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "scenario-agent")], "test"));
            return next(context);
        });
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        using var http = app.GetTestClient();
        McpToolSnapshot greeting;
        McpCallSnapshot actor;

        // Act
        McpCallSnapshot failure;

        // Assert
        await using (var scenario = await McpScenario.ConnectAsync(http, new Uri("http://localhost/mcp")))
        {
            var tools = await scenario.ListTools().ExpectExactly(
                "actors.read", "greetings.read", "greetings.reject");
            greeting = Assert.Single(tools, tool => tool.Name == "greetings.read");
            Assert.Equal("Reads a greeting without changing application state.", greeting.Description);
            Assert.Equal("Read greeting", greeting.Title);
            Assert.True(greeting.ReadOnly);
            Assert.Equal("object", greeting.InputSchema.GetProperty("type").GetString());

            var templates = await scenario.ListResourceTemplates().ExpectExactly("greetings://scenario/{name}");
            Assert.True(Assert.Single(templates).IsTemplate);
            var contents = await scenario.ReadResource("greetings://scenario/Portia")
                .Inspect(snapshots => Assert.Equal("Hello, Portia.", Assert.Single(snapshots).Text));
            Assert.Equal("Hello, Portia.", Assert.Single(contents).Text);

            Interlocked.Exchange(ref postRequests, 0);
            var resourceListing = scenario.ListResources()
                .ExpectExactly("greetings://local/fixed")
                .Inspect(snapshots => Assert.Equal("fixed greeting", Assert.Single(snapshots).Name));
            var resources = await resourceListing;
            Assert.Same(resources, await resourceListing);
            Assert.Equal(1, Volatile.Read(ref postRequests));

            var prompts = await scenario.ListPrompts().ExpectExactly("scenario-greeting");
            Assert.True(Assert.Single(prompts).Arguments["name"]);
            var messages = await scenario.GetPrompt("scenario-greeting",
                new Dictionary<string, object?> { ["name"] = "Portia" })
                .Inspect(snapshots => Assert.Equal("Hello, Portia.", Assert.Single(snapshots).Text));
            Assert.Equal("User", Assert.Single(messages).Role);

            actor = await scenario.When("actors.read")
                .ExpectResult(JsonSerializerOptions.Default.GetTypeInfo<string>(), value =>
                    Assert.Equal("scenario-agent|mcp|actors.read", value));
            Assert.Equal("scenario-agent|mcp|actors.read",
                actor.StructuredJson?.GetProperty("result").GetString());

            failure = await scenario.When("greetings.reject").ExpectFailure("Conflict");
            Assert.Null(failure.StructuredJson);
            Assert.Equal("Greeting already exists.", failure.Error?.GetProperty("message").GetString());

            var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await scenario.When("greetings.reject").ExpectFailure("NotFound"));
            Assert.Contains("'Conflict'", mismatch.Message, StringComparison.Ordinal);
            Assert.Contains("Greeting already exists.", mismatch.Message, StringComparison.Ordinal);

            var malformed = await scenario.When("greetings.read", new Dictionary<string, object?>
            {
                ["name"] = 42
            }).ExpectFailure("Binding");
            Assert.True(malformed.IsError);
        }

        Assert.Equal("object", greeting.InputSchema.GetProperty("type").GetString());
        Assert.Equal("scenario-agent|mcp|actors.read", actor.StructuredJson?.GetProperty("result").GetString());
        Assert.Equal("Greeting already exists.", failure.Error?.GetProperty("message").GetString());

        using var response = await http.GetAsync("/not-found");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    static string GetStdioHostPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                            ?? throw new InvalidOperationException("Missing test configuration.");
        return Path.GetFullPath(
            $"../../../../../smoke/Portia.McpStdioHost/bin/{configuration}/net11.0/Portia.McpStdioHost.dll",
            AppContext.BaseDirectory);
    }

    static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }

        return false;
    }

    static void KillProcessIfRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
    }
}

[CollectionDefinition("MCP HTTP integration", DisableParallelization = true)]
public sealed class McpHttpIntegrationGroup;

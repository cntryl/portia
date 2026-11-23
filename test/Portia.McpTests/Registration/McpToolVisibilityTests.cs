using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cntryl.Portia.Tests;

[Collection("MCP HTTP integration")]
public sealed class McpToolVisibilityTests
{
    const string ReadTool = "greetings.read";
    const string DocumentedTool = "greetings.documented";

    [Fact]
    public async Task ShouldListAndCallToolGivenPolicyAcceptsActor()
    {
        // Arrange
        await using var host = await HostAsync(
            services => services.AddSingleton<ByActorPolicy>(),
            application => application
                .AddMcpTool<McpRegistrationTests.ReadGreeting>(tool => tool.VisibleTo<ByActorPolicy>())
                .AddMcpTool<McpRegistrationTests.DocumentedGreeting>(tool => tool.Authenticated()));
        await using var owner = await host.ClientAsync("owner");

        // Act
        var tools = await owner.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await owner.CallToolAsync(ReadTool, new Dictionary<string, object?> { ["Name"] = "Portia" },
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([DocumentedTool, ReadTool], tools.Select(tool => tool.Name).Order());
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ShouldHideToolAndFailLikeUnknownToolGivenPolicyRejectsActor()
    {
        // Arrange
        await using var host = await HostAsync(
            services => services.AddSingleton<ByActorPolicy>(),
            application => application
                .AddMcpTool<McpRegistrationTests.ReadGreeting>(tool => tool.VisibleTo<ByActorPolicy>())
                .AddMcpTool<McpRegistrationTests.DocumentedGreeting>(tool => tool.Authenticated()));
        await using var outsider = await host.ClientAsync("outsider");
        var arguments = new Dictionary<string, object?> { ["Name"] = "Portia" };

        // Act
        var tools = await outsider.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var hidden = await Record.ExceptionAsync(async () => await outsider.CallToolAsync(ReadTool, arguments,
            cancellationToken: TestContext.Current.CancellationToken));
        var unknown = await Record.ExceptionAsync(async () => await outsider.CallToolAsync("greetings.missing",
            arguments, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal([DocumentedTool], tools.Select(tool => tool.Name));
        var hiddenError = Assert.IsType<McpProtocolException>(hidden);
        var unknownError = Assert.IsType<McpProtocolException>(unknown);
        Assert.Equal(unknownError.ErrorCode, hiddenError.ErrorCode);
        Assert.Equal(unknownError.Message.Replace("greetings.missing", ReadTool, StringComparison.Ordinal),
            hiddenError.Message);
    }

    [Fact]
    public async Task ShouldKeepOldStylePolicyWorkingGivenActorOnlyPolicy()
    {
        // Arrange
        await using var host = await HostAsync(
            services => services.AddSingleton<ActorOnlyPolicy>(),
            application => application
                .AddMcpTool<McpRegistrationTests.ReadGreeting>(tool => tool.VisibleTo<ActorOnlyPolicy>()));
        await using var owner = await host.ClientAsync("owner");
        await using var outsider = await host.ClientAsync("outsider");

        // Act
        var ownerTools = await owner.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var outsiderTools = await outsider.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(ownerTools);
        Assert.Empty(outsiderTools);
    }

    [Fact]
    public async Task ShouldReceiveEntryDescriptorGivenToolResourceAndPrompt()
    {
        // Arrange
        var recorder = new RecordingPolicy();
        await using var host = await HostAsync(
            services => services.AddSingleton(recorder),
            application => application
                .AddMcpTool<McpRegistrationTests.ReadGreeting>(tool => tool.VisibleTo<RecordingPolicy>())
                .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://local/{name}",
                    values => new McpRegistrationTests.ReadGreeting(values["name"]),
                    options =>
                    {
                        options.VisibleTo<RecordingPolicy>();
                        options.AsJson();
                    })
                .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("greet",
                    values => new McpRegistrationTests.ReadGreeting(values["name"]),
                    value => [new McpPromptMessage("user", value)],
                    options =>
                    {
                        options.VisibleTo<RecordingPolicy>();
                        options.Required("name");
                    }));
        await using var client = await host.ClientAsync("owner");

        // Act
        _ = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        _ = await client.ListResourceTemplatesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _ = await client.ListPromptsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var requestType = typeof(McpRegistrationTests.ReadGreeting);
        Assert.Equal(
            [
                new McpEntryDescriptor(McpEntryKind.Tool, ReadTool, requestType),
                new McpEntryDescriptor(McpEntryKind.Resource, "greetings://local/{name}", requestType),
                new McpEntryDescriptor(McpEntryKind.Prompt, "greet", requestType)
            ],
            recorder.Entries.ToArray());
    }

    [Fact]
    public async Task ShouldEvaluatePolicyOncePerToolGivenOneListCall()
    {
        // Arrange
        var recorder = new RecordingPolicy();
        await using var host = await HostAsync(
            services => services.AddSingleton(recorder),
            application => application
                .AddMcpTool<McpRegistrationTests.ReadGreeting>(tool => tool.VisibleTo<RecordingPolicy>())
                .AddMcpTool<McpRegistrationTests.DocumentedGreeting>(tool => tool.VisibleTo<RecordingPolicy>()));
        await using var client = await host.ClientAsync("owner");

        // Act
        _ = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, recorder.Entries.Count);
        Assert.Equal(2, recorder.Entries.Select(entry => entry.Name).Distinct().Count());
    }

    [Fact]
    public async Task ShouldHideToolGivenPolicyThrows()
    {
        // Arrange
        await using var host = await HostAsync(
            services => services.AddSingleton<ThrowingPolicy>(),
            application => application
                .AddMcpTool<McpRegistrationTests.ReadGreeting>(tool => tool.VisibleTo<ThrowingPolicy>()));
        await using var client = await host.ClientAsync("owner");

        // Act
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var call = await Record.ExceptionAsync(async () => await client.CallToolAsync(ReadTool,
            new Dictionary<string, object?> { ["Name"] = "Portia" },
            cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Empty(tools);
        Assert.IsType<McpProtocolException>(call);
    }

    [Fact]
    public async Task ShouldPropagateCancellationGivenCallerCancelsDuringPolicy()
    {
        // Arrange
        var policy = new BlockingPolicy();
        await using var host = await HostAsync(
            services => services.AddSingleton(policy),
            application => application
                .AddMcpTool<McpRegistrationTests.ReadGreeting>(tool => tool.VisibleTo<BlockingPolicy>()));
        await using var client = await host.ClientAsync("owner");
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        var listing = client.ListToolsAsync(cancellationToken: caller.Token).AsTask();
        await policy.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await caller.CancelAsync();
        var error = await Record.ExceptionAsync(async () => await listing);

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        await policy.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ShouldHideAuthenticatedToolGivenAnonymousCallerAndShowPublicAndUnchosenTools()
    {
        // Arrange
        await using var host = await HostAsync(
            _ => { },
            application => application
                .AddMcpTool<McpRegistrationTests.ReadGreeting>(tool => tool.Public())
                .AddMcpTool<McpRegistrationTests.DocumentedGreeting>(tool => tool.Authenticated())
                .AddMcpTool<McpRegistrationTests.RejectGreeting>());
        await using var anonymous = await host.ClientAsync(null);
        await using var signedIn = await host.ClientAsync("owner");

        // Act
        var anonymousTools = await anonymous.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var signedInTools = await signedIn.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var anonymousCall = await Record.ExceptionAsync(async () => await anonymous.CallToolAsync(DocumentedTool,
            new Dictionary<string, object?> { ["greeting_name"] = "Portia" },
            cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal([ReadTool, "greetings.reject"], anonymousTools.Select(tool => tool.Name).Order());
        Assert.Equal([DocumentedTool, ReadTool, "greetings.reject"], signedInTools.Select(tool => tool.Name).Order());
        Assert.IsType<McpProtocolException>(anonymousCall);
    }

    [Fact]
    public async Task ShouldJudgeResourceAndPromptByNameGivenDescriptorPolicy()
    {
        // Arrange
        await using var host = await HostAsync(
            services => services.AddSingleton<NameStartsWithAllowedPolicy>(),
            application => application
                .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://allowed/{name}",
                    values => new McpRegistrationTests.ReadGreeting(values["name"]),
                    options =>
                    {
                        options.VisibleTo<NameStartsWithAllowedPolicy>();
                        options.AsJson();
                    })
                .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://denied/{name}",
                    values => new McpRegistrationTests.ReadGreeting(values["name"]),
                    options =>
                    {
                        options.VisibleTo<NameStartsWithAllowedPolicy>();
                        options.AsJson();
                    })
                .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("allowed-prompt",
                    values => new McpRegistrationTests.ReadGreeting(values["name"]),
                    value => [new McpPromptMessage("user", value)],
                    options =>
                    {
                        options.VisibleTo<NameStartsWithAllowedPolicy>();
                        options.Required("name");
                    })
                .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("denied-prompt",
                    values => new McpRegistrationTests.ReadGreeting(values["name"]),
                    value => [new McpPromptMessage("user", value)],
                    options =>
                    {
                        options.VisibleTo<NameStartsWithAllowedPolicy>();
                        options.Required("name");
                    }));
        await using var client = await host.ClientAsync("owner");

        // Act
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var prompts = await client.ListPromptsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var denied = await Record.ExceptionAsync(async () => await client.ReadResourceAsync(
            "greetings://denied/Portia", cancellationToken: TestContext.Current.CancellationToken));
        var allowed = await client.ReadResourceAsync("greetings://allowed/Portia",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["greetings://allowed/{name}"], templates.Select(template => template.UriTemplate));
        Assert.Equal(["allowed-prompt"], prompts.Select(prompt => prompt.Name));
        Assert.NotNull(denied);
        Assert.Single(allowed.Contents);
    }

    static async Task<TestHost> HostAsync(Action<IServiceCollection> services, Func<PortiaBuilder, PortiaBuilder> declare)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        services(builder.Services);
        var application = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddRequestHandler<McpRegistrationTests.DocumentedGreetingHandler>()
            .AddRequestHandler<McpRegistrationTests.RejectGreetingHandler>();
        _ = declare(application).AddMcpHttp();
        var app = builder.Build();
        app.Use((context, next) =>
        {
            var name = context.Request.Headers["X-Actor"].ToString();
            if (name.Length > 0)
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"));
            return next(context);
        });
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new TestHost(app);
    }

    sealed class TestHost(WebApplication app) : IAsyncDisposable
    {
        public async Task<McpClient> ClientAsync(string? actor)
        {
            var http = app.GetTestClient();
            if (actor is not null)
                http.DefaultRequestHeaders.Add("X-Actor", actor);
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp")
            }, http, null);
            return await McpClient.CreateAsync(transport,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    public sealed class ByActorPolicy : IMcpVisibilityPolicy
    {
        public ValueTask<bool> IsVisibleAsync(ClaimsPrincipal actor, McpEntryDescriptor entry,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(entry.Name != ReadTool || actor.Identity?.Name == "owner");
    }

    public sealed class ActorOnlyPolicy : IMcpVisibilityPolicy
    {
        public ValueTask<bool> IsVisibleAsync(ClaimsPrincipal actor, CancellationToken cancellationToken) =>
            ValueTask.FromResult(actor.Identity?.Name == "owner");
    }

    public sealed class RecordingPolicy : IMcpVisibilityPolicy
    {
        public List<McpEntryDescriptor> Entries { get; } = [];

        public ValueTask<bool> IsVisibleAsync(ClaimsPrincipal actor, McpEntryDescriptor entry,
            CancellationToken cancellationToken)
        {
            lock (Entries)
                Entries.Add(entry);
            return ValueTask.FromResult(true);
        }
    }

    public sealed class ThrowingPolicy : IMcpVisibilityPolicy
    {
        public ValueTask<bool> IsVisibleAsync(ClaimsPrincipal actor, McpEntryDescriptor entry,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Policy failure.");
    }

    public sealed class BlockingPolicy : IMcpVisibilityPolicy
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<bool> IsVisibleAsync(ClaimsPrincipal actor, McpEntryDescriptor entry,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                throw;
            }

            return true;
        }
    }

    public sealed class NameStartsWithAllowedPolicy : IMcpVisibilityPolicy
    {
        public ValueTask<bool> IsVisibleAsync(ClaimsPrincipal actor, McpEntryDescriptor entry,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(entry.Name.Contains("allowed", StringComparison.Ordinal));
    }
}

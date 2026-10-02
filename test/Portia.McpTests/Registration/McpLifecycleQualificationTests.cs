using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Cntryl.Portia.McpContracts;
using Cntryl.Portia.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cntryl.Portia.Tests;

[Collection("MCP HTTP integration")]
public sealed class McpLifecycleQualificationTests
{
    [Theory]
    [InlineData("tool", "2026-07-28", false)]
    [InlineData("resource", "2026-07-28", false)]
    [InlineData("prompt", "2026-07-28", false)]
    [InlineData("tool", "2025-11-25", false)]
    [InlineData("resource", "2025-11-25", false)]
    [InlineData("prompt", "2025-11-25", false)]
    [InlineData("tool", "2026-07-28", true)]
    [InlineData("resource", "2026-07-28", true)]
    [InlineData("prompt", "2026-07-28", true)]
    [InlineData("tool", "2025-11-25", true)]
    [InlineData("resource", "2025-11-25", true)]
    [InlineData("prompt", "2025-11-25", true)]
    public async Task ShouldCancelMinimalSdkStdioHandler(string primitive, string revision, bool explicitNotification)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var evidence = Path.Combine(Path.GetTempPath(), $"portia-sdk-cancellation-{primitive}-{revision}-{explicitNotification}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        using var logs = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(new ProtocolEvidenceLogger(evidence)));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var host = Path.GetFullPath($"../../../../../smoke/Portia.McpStdioHost/bin/{configuration}/net10.0/Portia.McpStdioHost.dll", AppContext.BaseDirectory);
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [host, "--sdk-cancellation", "--evidence-dir", evidence],
            ShutdownTimeout = TimeSpan.FromSeconds(1)
        }), new McpClientOptions { ProtocolVersion = revision }, loggerFactory: logs, cancellationToken: timeout.Token);
        using var cancellation = new CancellationTokenSource();
        var operation = InvokeAsync();
        await PollAsync("started");
        var request = File.ReadAllLines(Path.Combine(evidence, "ingress.tsv"))
            .Select(line => line.Split('\t')).Single(fields => fields.Length == 3 && fields[0] == "request"
                && fields[2] is "tools/call" or "resources/read" or "prompts/get");
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        if (explicitNotification)
            await client.SendMessageAsync(new JsonRpcNotification
            {
                Method = NotificationMethods.CancelledNotification,
                Params = new System.Text.Json.Nodes.JsonObject { ["requestId"] = long.Parse(request[1], System.Globalization.CultureInfo.InvariantCulture) }
            }, timeout.Token);
        await PollAsync("disposed");
        Assert.True(File.Exists(Path.Combine(evidence, "canceled")), evidence);
        var notification = File.ReadAllLines(Path.Combine(evidence, "ingress.tsv"))
            .Select(line => line.Split('\t')).First(fields => fields.Length == 3 && fields[0] == "notification"
                && fields[1] == NotificationMethods.CancelledNotification);
        using var parameters = JsonDocument.Parse(notification[2]);
        Assert.Equal(long.Parse(request[1], System.Globalization.CultureInfo.InvariantCulture), parameters.RootElement.GetProperty("requestId").GetInt64());

        async Task PollAsync(string marker)
        {
            try
            {
                while (!File.Exists(Path.Combine(evidence, marker)))
                    await Task.Delay(20, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"SDK {revision} {primitive}: missing {marker}; protocol evidence: {evidence}");
            }
        }

        async Task InvokeAsync()
        {
            if (primitive == "tool")
                _ = await client.CallToolAsync("wait", cancellationToken: cancellation.Token);
            else if (primitive == "resource")
                _ = await client.ReadResourceAsync("wait://value", cancellationToken: cancellation.Token);
            else
                _ = await client.GetPromptAsync("wait", cancellationToken: cancellation.Token);
        }
    }

    [Fact]
    public async Task ShouldPreserveHttpConfigurationWhileOwningOperationScopes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var cancellation = new CancellationTokenSource();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var calls = 0;
        _ = QualifiedApplication.Add(services).AddMcpTool<QualifiedRead>().AddMcpHttp(options =>
            options.ConfigureSessionOptions = (actualContext, serverOptions, token) =>
            {
                Assert.Same(context, actualContext);
                Assert.Equal(cancellation.Token, token);
                calls++;
                serverOptions.ServerInstructions = "application instructions";
                return Task.CompletedTask;
            });
        await using var provider = services.BuildServiceProvider();
        var transport = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelContextProtocol.AspNetCore.HttpServerTransportOptions>>().Value;
        var configured = new ModelContextProtocol.Server.McpServerOptions { ScopeRequests = false };
        await transport.ConfigureSessionOptions!(context, configured, cancellation.Token);
        Assert.Equal(1, calls);
        Assert.Equal("application instructions", configured.ServerInstructions);
        Assert.True(configured.ScopeRequests);
    }

    static readonly string[] EvidenceFiles = ["spans.tsv", "metrics.tsv", "logs.tsv"];
    [Theory]
    [InlineData("http", "tool", false, false)]
    [InlineData("http", "resource", false, false)]
    [InlineData("http", "prompt", false, false)]
    [InlineData("stdio", "tool", false, false)]
    [InlineData("stdio", "resource", false, false)]
    [InlineData("stdio", "prompt", false, false)]
    [InlineData("http", "tool", true, false)]
    [InlineData("http", "resource", true, false)]
    [InlineData("http", "prompt", true, false)]
    [InlineData("stdio", "tool", true, false)]
    [InlineData("stdio", "resource", true, false)]
    [InlineData("stdio", "prompt", true, false)]
    [InlineData("stdio", "tool", false, true)]
    [InlineData("stdio", "resource", false, true)]
    [InlineData("stdio", "prompt", false, true)]
    public async Task ShouldPropagateCancellationAndSanitizeDeadlinesOnBothTransports(string transportName,
        string primitive, bool deadline, bool explicitNotification)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
        var application = QualifiedApplication.Add(builder.Services).AddMcpTool<QualifiedRead>()
            .AddMcpResource<QualifiedRead, QualifiedState>("qualification://wait/{tenant}",
                values => new QualifiedRead(values["tenant"], Wait: true), options => { options.Authenticated(); options.AsJson(); })
            .AddMcpPrompt<QualifiedRead, QualifiedState>("qualification-wait", values => new QualifiedRead(values["tenant"], Wait: true),
                _ => [new McpPromptMessage("user", "completed")], options => { options.Authenticated(); options.Required("tenant"); })
            .AddMcpHttp();
        if (deadline)
            _ = application.ConfigureMcpLimits(limits => limits.OperationDeadline = TimeSpan.FromMilliseconds(250));
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, QualifiedObservations.Tenant)], "test"));
            return next(context);
        });
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync(token);
        var evidence = Path.Combine(Path.GetTempPath(), $"portia-lifecycle-{transportName}-{primitive}-{deadline}-{explicitNotification}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        using var protocolLogs = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(new ProtocolEvidenceLogger(evidence)));
        await using var client = await ConnectAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var operation = InvokeWaitAsync(cancellation.Token);
        await PollAsync(async () => (await StateAsync()).WaitStarted == 1);
        if (deadline)
        {
            var error = await Record.ExceptionAsync(async () => await operation);
            if (primitive != "tool")
            {
                Assert.NotNull(error);
                Assert.DoesNotContain("sensitive", error.Message, StringComparison.Ordinal);
            }
            else
                Assert.Null(error);
        }
        else
        {
            await cancellation.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
            if (explicitNotification)
            {
                var method = primitive == "tool" ? RequestMethods.ToolsCall : primitive == "resource" ? RequestMethods.ResourcesRead : RequestMethods.PromptsGet;
                var request = File.ReadAllLines(Path.Combine(evidence, "ingress.tsv"))
                    .Select(line => line.Split('\t')).First(fields => fields.Length == 4 && fields[0] == "request" && fields[2] == method
                        && (primitive != "tool" || IsWaitRequest(fields[3])));
                await client.SendMessageAsync(new JsonRpcNotification
                {
                    Method = NotificationMethods.CancelledNotification,
                    Params = new System.Text.Json.Nodes.JsonObject { ["requestId"] = long.Parse(request[1], System.Globalization.CultureInfo.InvariantCulture) }
                }, token);
            }
        }
        await PollAsync(async () =>
        {
            var observed = await StateAsync();
            return observed.WaitCanceled == 1 && observed.Scopes.Any(scope => scope.Stages.Contains("wait", StringComparer.Ordinal)
                && scope.Stages.Contains("disposed", StringComparer.Ordinal));
        });
        var state = await StateAsync();
        Assert.Empty(state.Values);
        Assert.Equal(1, state.WaitStarted);
        Assert.Equal(1, state.WaitCanceled);
        if (transportName == "stdio")
        {
            await InvokeFaultAsync();
            await PollAsync(() => Task.FromResult(HasExportedEvidence()));
            var spans = File.ReadAllLines(Path.Combine(evidence, "spans.tsv")).Select(line => line.Split('\t')).ToArray();
            var logs = File.ReadAllLines(Path.Combine(evidence, "logs.tsv")).Select(line => line.Split('\t')).ToArray();
            var log = Assert.Single(logs);
            Assert.Equal("1002", log[0]);
            Assert.Equal(typeof(IOException).FullName, log[3]);
            Assert.Contains(spans, span => span[0] == PortiaTelemetry.ProcessActivityName && span[1] == log[1]
                && span[2] == log[2] && span[4] == "fault");
            Assert.Contains(spans, span => span[0] == PortiaTelemetry.ExecuteActivityName && span[4] == "canceled");
            foreach (var execute in spans.Where(span => span[0] == PortiaTelemetry.ExecuteActivityName))
                Assert.Contains(spans, process => process[0] == PortiaTelemetry.ProcessActivityName
                    && process[1] == execute[1] && process[2] == execute[3]);
            Assert.Equal(2 * spans.Count(span => span[0] == PortiaTelemetry.ExecuteActivityName), spans.Length);
        }

        static bool IsWaitRequest(string parameters)
        {
            using var parsed = JsonDocument.Parse(parameters);
            return parsed.RootElement.GetProperty("arguments").TryGetProperty("wait", out var wait) && wait.GetBoolean();
        }

        async Task<McpClient> ConnectAsync()
        {
            if (transportName == "http")
                return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
                { Endpoint = new Uri("http://localhost/mcp") }, app.GetTestClient()), loggerFactory: protocolLogs, cancellationToken: token);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var host = Path.GetFullPath(
                $"../../../../../smoke/Portia.McpStdioHost/bin/{configuration}/net10.0/Portia.McpStdioHost.dll", AppContext.BaseDirectory);
            var arguments = new List<string> { host, "--qualification", "--evidence-dir", evidence };
            if (deadline)
                arguments.Add("--short-deadline");
            return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            { Command = "dotnet", Arguments = arguments, Name = "MCP lifecycle qualification", ShutdownTimeout = TimeSpan.FromSeconds(1) }), loggerFactory: protocolLogs, cancellationToken: token);
        }

        async Task InvokeWaitAsync(CancellationToken ct)
        {
            if (primitive == "tool")
            {
                var result = await client.CallToolAsync("qualification.read", new Dictionary<string, object?>
                { ["tenant"] = QualifiedObservations.Tenant, ["wait"] = true }, cancellationToken: ct);
                Assert.True(deadline && result.IsError == true);
                Assert.Null(result.StructuredContent);
            }
            else if (primitive == "resource")
                _ = await client.ReadResourceAsync($"qualification://wait/{QualifiedObservations.Tenant}", cancellationToken: ct);
            else
                _ = await client.GetPromptAsync("qualification-wait", new Dictionary<string, object?>
                { ["tenant"] = QualifiedObservations.Tenant }, cancellationToken: ct);
        }

        async Task InvokeFaultAsync()
        {
            if (primitive == "tool")
            {
                var result = await client.CallToolAsync("qualification.read", new Dictionary<string, object?>
                { ["tenant"] = QualifiedObservations.Tenant, ["fail"] = true }, cancellationToken: token);
                Assert.True(result.IsError);
                Assert.Null(result.StructuredContent);
            }
            else if (primitive == "resource")
                _ = await Assert.ThrowsAnyAsync<Exception>(async () => await client.ReadResourceAsync(
                    $"qualification://failure/{QualifiedObservations.Tenant}", cancellationToken: token));
            else
                _ = await Assert.ThrowsAnyAsync<Exception>(async () => await client.GetPromptAsync("qualification-failure",
                    new Dictionary<string, object?> { ["tenant"] = QualifiedObservations.Tenant }, cancellationToken: token));
        }

        async Task<QualifiedState> StateAsync()
        {
            var result = await client.CallToolAsync("qualification.read", new Dictionary<string, object?>
            { ["tenant"] = QualifiedObservations.Tenant }, cancellationToken: token);
            var json = app.Services.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
            return (QualifiedState)result.StructuredContent!.Value.GetProperty("result")
                .Deserialize(json.GetTypeInfo(typeof(QualifiedState)))!;
        }

        async Task PollAsync(Func<Task<bool>> condition)
        {
            using var polling = CancellationTokenSource.CreateLinkedTokenSource(token);
            polling.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (!await condition())
                    await Task.Delay(20, polling.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"{transportName} {primitive}: lifecycle condition did not complete; evidence: {evidence}");
            }
        }

        bool HasExportedEvidence()
        {
            var paths = EvidenceFiles.Select(name => Path.Combine(evidence, name)).ToArray();
            if (paths.Any(path => !File.Exists(path)))
                return false;
            var spans = File.ReadAllLines(paths[0]);
            var metrics = File.ReadAllLines(paths[1]);
            return File.ReadAllLines(paths[2]).Length == 1
                && spans.Any(line => line.Contains("\tfault\t", StringComparison.Ordinal))
                && spans.Any(line => line.Contains("\tcanceled\t", StringComparison.Ordinal))
                && metrics.Any(line => line.StartsWith("portia.worker.failure\t", StringComparison.Ordinal))
                && metrics.Any(line => line.Contains("portia.outcome=canceled", StringComparison.Ordinal));
        }
    }
}

file sealed class ProtocolEvidenceLogger(string directory) : ILoggerProvider
{
    readonly object _gate = new();
    public ILogger CreateLogger(string categoryName) => new EvidenceLogger(this, categoryName);
    public void Dispose() { }
    sealed class EvidenceLogger(ProtocolEvidenceLogger owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._gate)
                File.AppendAllText(Path.Combine(owner.Directory, "client.log"), category + " " + formatter(state, exception) + "\n");
        }
    }
    internal string Directory => directory;
}

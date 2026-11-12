using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cntryl.Portia;

/// <summary>Runs a reproducible end-to-end MCP transport qualification campaign.</summary>
public static class McpProtocolQualification
{
    static long AdapterRequestId;
    static readonly int[] CatalogSizes = [1, 100, 1000];
    static readonly int[] PayloadSizes = [256, 4096, 65536];
    static readonly int[] NestedValues = [3, 5, 8];
    static readonly string[] TelemetryModes = ["disabled", "propagation", "sampled-tracing", "metrics", "logging"];
    static readonly ClaimsPrincipal Actor = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "mcp-benchmark")], "benchmark"));

    /// <summary>Runs the full campaign, or a short diagnostic sample, and writes JSONL evidence.</summary>
    public static async Task RunAsync(string runMode, string outputPath)
    {
        var full = string.Equals(runMode, "full", StringComparison.Ordinal);
        if (!full && !string.Equals(runMode, "diagnostic", StringComparison.Ordinal))
            throw new ArgumentException("Run mode must be 'full' or 'diagnostic'.", nameof(runMode));

        var plan = new CampaignPlan(full ? 10 : 1, full ? 30 : 3, full ? 50 : 3);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var metadata = await MetadataAsync(runMode, plan);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await using var output = new StreamWriter(fullOutputPath, false, new UTF8Encoding(false));
        output.AutoFlush = true;
        await output.WriteLineAsync(JsonSerializer.Serialize(metadata));

        var catalogSizes = CatalogSizes;
        var telemetryModes = TelemetryModes;
        foreach (var telemetryMode in telemetryModes)
        {
            foreach (var catalogSize in catalogSizes)
            {
                await using var server = await McpBenchmarkServer.StartHttpAsync(catalogSize, telemetryMode);
                await using var adapter = server.CreateAdapterOnlySession();
                await WriteMeasurementAsync(output, "http", catalogSize, telemetryMode, "startup", 0,
                    Measurement.Single(server.StartupMilliseconds));

                await MeasureAdapterOnlyAsync(output, server, adapter, catalogSize, telemetryMode, plan);
                await MeasureApplicationControlsAsync(output, server, catalogSize, telemetryMode, plan);
                await using var httpClient = await McpBenchmarkClient.ConnectHttpAsync(server.Endpoint);
                await WriteMeasurementAsync(output, "http", catalogSize, telemetryMode, "negotiation", 0,
                    Measurement.Single(httpClient.NegotiationMilliseconds));
                await MeasureMcpAsync(output, httpClient, "http", catalogSize, telemetryMode, plan, server);

                var stdioStarted = Stopwatch.GetTimestamp();
                await using var stdioClient = await McpBenchmarkClient.ConnectStdioAsync(catalogSize, telemetryMode);
                var stdioStartupAndNegotiation = Stopwatch.GetElapsedTime(stdioStarted).TotalMilliseconds;
                await WriteMeasurementAsync(output, "stdio", catalogSize, telemetryMode,
                    "startup-and-negotiation", 0, Measurement.Single(stdioStartupAndNegotiation));
                await MeasureDiscoveryAsync(output, stdioClient, "stdio", catalogSize, telemetryMode, plan);
                await MeasureMcpOperationsAsync(output, stdioClient, "stdio", catalogSize, telemetryMode, plan);
            }
        }

        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            recordType = "complete",
            completedUtc = DateTimeOffset.UtcNow,
            outputPath = fullOutputPath,
            mode = runMode,
            configurations = TelemetryModes.Length * CatalogSizes.Length * 2
        }));
    }

    /// <summary>Verifies benchmark payload lengths over real HTTP and stdio MCP transports.</summary>
    public static async Task RunPayloadQualificationAsync()
    {
        const int catalogSize = 3;
        await using var server = await McpBenchmarkServer.StartHttpAsync(catalogSize, "disabled")
            .ConfigureAwait(false);
        await using var httpClient = await McpBenchmarkClient.ConnectHttpAsync(server.Endpoint)
            .ConfigureAwait(false);
        await httpClient.VerifyPayloadsAsync("Streamable HTTP", PayloadSizes).ConfigureAwait(false);

        await using var stdioClient = await McpBenchmarkClient.ConnectStdioAsync(catalogSize, "disabled")
            .ConfigureAwait(false);
        await stdioClient.VerifyPayloadsAsync("stdio", PayloadSizes).ConfigureAwait(false);
        Console.WriteLine("MCP payload qualification passed over Streamable HTTP and stdio.");
    }

    /// <summary>Measures fixed-resource, template-resource, and prompt lookup at each catalog size.</summary>
    public static async Task RunAdapterLookupQualificationAsync(string outputPath)
    {
        const int payloadBytes = 4096;
        var plan = new CampaignPlan(100, 500, 1000);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var metadata = await MetadataAsync("adapter-lookup", plan).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await using var output = new StreamWriter(fullOutputPath, false, new UTF8Encoding(false));
        output.AutoFlush = true;
        await output.WriteLineAsync(JsonSerializer.Serialize(metadata)).ConfigureAwait(false);

        foreach (var catalogSize in CatalogSizes)
        {
            await using var server = await McpBenchmarkServer.StartHttpAsync(
                catalogSize, "disabled", includeFixedLookupResource: true).ConfigureAwait(false);
            await using var adapter = server.CreateAdapterOnlySession();
            var readResource = adapter.Server.ServerOptions.Handlers.ReadResourceHandler
                ?? throw new InvalidOperationException("The MCP resource adapter was not registered.");
            var getPrompt = adapter.Server.ServerOptions.Handlers.GetPromptHandler
                ?? throw new InvalidOperationException("The MCP prompt adapter was not registered.");

            await MeasureResourceAsync("read-resource-fixed-json", "benchmark://fixed/lookup");
            await MeasureResourceAsync("read-resource-template-last-json",
                $"benchmark://resource/{catalogSize - 1:D4}/{payloadBytes.ToString(CultureInfo.InvariantCulture)}");
            await MeasurePromptAsync("get-prompt-first", "benchmark_prompt_0000");
            await MeasurePromptAsync("get-prompt-last", $"benchmark_prompt_{catalogSize - 1:D4}");

            async Task MeasureResourceAsync(string operation, string uri)
            {
                await WriteAdapterMeasuredAsync(output, server, adapter, operation, payloadBytes,
                    provider => new ModelContextProtocol.Server.RequestContext<ReadResourceRequestParams>(
                        adapter.Server, AdapterRequest(RequestMethods.ResourcesRead),
                        new ReadResourceRequestParams { Uri = uri })
                    {
                        Services = provider,
                        User = Actor
                    }, context => readResource(context, CancellationToken.None),
                    result =>
                    {
                        if (result.Contents.Count != 1 || result.Contents[0] is not TextResourceContents text ||
                            JsonElement.Parse(text.Text).GetProperty("data").GetString()?.Length != payloadBytes)
                            throw new InvalidOperationException("The direct MCP resource adapter returned an unexpected result.");
                    }, plan, catalogSize, "disabled").ConfigureAwait(false);
            }

            async Task MeasurePromptAsync(string operation, string name)
            {
                await WriteAdapterMeasuredAsync(output, server, adapter, operation, payloadBytes,
                    provider => new ModelContextProtocol.Server.RequestContext<GetPromptRequestParams>(
                        adapter.Server, AdapterRequest(RequestMethods.PromptsGet),
                        new GetPromptRequestParams
                        {
                            Name = name,
                            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                            {
                                ["size"] = JsonSerializer.SerializeToElement(
                                    payloadBytes.ToString(CultureInfo.InvariantCulture))
                            }
                        })
                    {
                        Services = provider,
                        User = Actor
                    }, context => getPrompt(context, CancellationToken.None),
                    result =>
                    {
                        if (result.Messages.Count != 1 || result.Messages[0].Content is not TextContentBlock text ||
                            text.Text.Length != payloadBytes)
                            throw new InvalidOperationException("The direct MCP prompt adapter returned an unexpected result.");
                    }, plan, catalogSize, "disabled").ConfigureAwait(false);
            }

            JsonRpcRequest AdapterRequest(string method) => new()
            {
                Id = new RequestId(Interlocked.Increment(ref AdapterRequestId)),
                Method = method
            };
        }

        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            recordType = "complete",
            completedUtc = DateTimeOffset.UtcNow,
            outputPath = fullOutputPath,
            mode = "adapter-lookup",
            configurations = CatalogSizes.Length * 4
        })).ConfigureAwait(false);
    }

    /// <summary>Starts the benchmark server used by the official stdio client transport.</summary>
    public static async Task RunStdioServerAsync(int catalogSize, string telemetryMode)
    {
        using var telemetry = BenchmarkTelemetry.Attach(telemetryMode);
        var builder = Host.CreateApplicationBuilder([]);
        ConfigureLogging(builder.Logging, telemetryMode, telemetry);
        builder.Services.AddSingleton(serviceProvider => new BenchmarkState(
            telemetryMode == "logging", serviceProvider.GetRequiredService<ILoggerFactory>()));
        var application = RegisterCatalog(builder.Services.AddPortia()
            .AddRequestHandler<BenchmarkRequestHandler>(), catalogSize);
        application.AddMcpStdio(options => options.UseLocalDevelopmentActor("mcp-benchmark"));
        using var host = builder.Build();
        await host.RunAsync();
    }

    static async Task MeasureApplicationControlsAsync(StreamWriter output, McpBenchmarkServer server,
        int catalogSize, string telemetryMode, CampaignPlan plan)
    {
        foreach (var size in PayloadSizes)
        {
            var request = BenchmarkRequest.ForSize(size);
            await WriteMeasuredAsync(output, server, "direct", catalogSize, telemetryMode, "request-bus",
                size, () => SendDirectAsync(server.Bus, request), plan);
            await WriteMeasuredAsync(output, server, "http", catalogSize, telemetryMode, "rest-control",
                size, () => server.PostRestAsync(request), plan);
        }
    }

    static async Task MeasureMcpAsync(StreamWriter output, McpBenchmarkClient client, string transport,
        int catalogSize, string telemetryMode, CampaignPlan plan, McpBenchmarkServer? server = null)
    {
        await MeasureDiscoveryAsync(output, client, transport, catalogSize, telemetryMode, plan, server);
        await MeasureMcpOperationsAsync(output, client, transport, catalogSize, telemetryMode, plan, server);
    }

    static async Task MeasureDiscoveryAsync(StreamWriter output, McpBenchmarkClient client, string transport,
        int catalogSize, string telemetryMode, CampaignPlan plan, McpBenchmarkServer? server = null)
    {
        await WriteMeasuredAsync(output, server, transport, catalogSize, telemetryMode, "discover-tools",
            0, () => client.DiscoverToolsAsync(catalogSize), plan);
        await WriteMeasuredAsync(output, server, transport, catalogSize, telemetryMode, "discover-resources",
            0, () => client.DiscoverResourcesAsync(catalogSize), plan);
        await WriteMeasuredAsync(output, server, transport, catalogSize, telemetryMode, "discover-prompts",
            0, () => client.DiscoverPromptsAsync(catalogSize), plan);
    }

    static async Task MeasureMcpOperationsAsync(StreamWriter output, McpBenchmarkClient client, string transport,
        int catalogSize, string telemetryMode, CampaignPlan plan, McpBenchmarkServer? server = null)
    {
        foreach (var size in PayloadSizes)
        {
            await WriteMeasuredAsync(output, server, transport, catalogSize, telemetryMode, "tool-call",
                size, () => client.CallToolAsync(size), plan);
            await WriteMeasuredAsync(output, server, transport, catalogSize, telemetryMode, "render-prompt",
                size, () => client.GetPromptAsync(size), plan);
            var formatCount = Math.Min(catalogSize, 3);
            for (var formatIndex = 0; formatIndex < formatCount; formatIndex++)
                await WriteMeasuredAsync(output, server, transport, catalogSize, telemetryMode,
                    $"read-resource-{ResourceFormat.ForIndex(formatIndex).Name}", size,
                    () => client.ReadResourceAsync(formatIndex, size), plan);
        }
    }

    static async Task MeasureAdapterOnlyAsync(StreamWriter output, McpBenchmarkServer server,
        McpAdapterOnlySession adapter, int catalogSize, string telemetryMode, CampaignPlan plan)
    {
        var tool = (adapter.Server.ServerOptions.ToolCollection
            ?? throw new InvalidOperationException("The MCP tool adapter catalog was not registered."))
            .Single(candidate => candidate.ProtocolTool.Name == "benchmark.tool.0000");
        var readResource = adapter.Server.ServerOptions.Handlers.ReadResourceHandler
            ?? throw new InvalidOperationException("The MCP resource adapter was not registered.");
        var getPrompt = adapter.Server.ServerOptions.Handlers.GetPromptHandler
            ?? throw new InvalidOperationException("The MCP prompt adapter was not registered.");

        foreach (var size in PayloadSizes)
        {
            var toolArguments = ToolArguments(size);
            await WriteAdapterMeasuredAsync(output, server, adapter, "tool-call", size,
                provider => new ModelContextProtocol.Server.RequestContext<CallToolRequestParams>(
                    adapter.Server, AdapterRequest(RequestMethods.ToolsCall),
                    new CallToolRequestParams
                    {
                        Name = tool.ProtocolTool.Name,
                        Arguments = toolArguments
                    })
                {
                    MatchedPrimitive = tool,
                    Services = provider,
                    User = Actor
                },
                context => tool.InvokeAsync(context),
                result =>
                {
                    if (result.IsError == true || result.StructuredContent is not { } structured ||
                        !structured.TryGetProperty("result", out var value) ||
                        !value.TryGetProperty("data", out var data) || data.GetString()?.Length != size)
                        throw new InvalidOperationException("The direct MCP tool adapter did not return the expected structured result.");
                }, plan, catalogSize, telemetryMode);

            await WriteAdapterMeasuredAsync(output, server, adapter, "render-prompt", size,
                provider => new ModelContextProtocol.Server.RequestContext<GetPromptRequestParams>(
                    adapter.Server, AdapterRequest(RequestMethods.PromptsGet),
                    new GetPromptRequestParams
                    {
                        Name = "benchmark_prompt_0000",
                        Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["size"] = JsonSerializer.SerializeToElement(size.ToString(CultureInfo.InvariantCulture))
                        }
                    })
                {
                    Services = provider,
                    User = Actor
                }, context => getPrompt(context, CancellationToken.None),
                result =>
                {
                    if (result.Messages.Count != 1 || result.Messages[0].Content is not TextContentBlock text ||
                        text.Text.Length != size)
                        throw new InvalidOperationException("The direct MCP prompt adapter did not render the expected text.");
                }, plan, catalogSize, telemetryMode);

            var formatCount = Math.Min(catalogSize, 3);
            for (var formatIndex = 0; formatIndex < formatCount; formatIndex++)
            {
                var uri = $"benchmark://resource/{formatIndex:D4}/{size.ToString(CultureInfo.InvariantCulture)}";
                var format = ResourceFormat.ForIndex(formatIndex);
                await WriteAdapterMeasuredAsync(output, server, adapter, $"read-resource-{format.Name}", size,
                    provider => new ModelContextProtocol.Server.RequestContext<ReadResourceRequestParams>(
                        adapter.Server, AdapterRequest(RequestMethods.ResourcesRead),
                        new ReadResourceRequestParams { Uri = uri })
                    {
                        Services = provider,
                        User = Actor
                    }, context => readResource(context, CancellationToken.None),
                    result =>
                    {
                        var contentMatches = result.Contents.Count == 1 && format.Kind switch
                        {
                            ResourceContentKind.Json => result.Contents[0] is TextResourceContents json &&
                                JsonElement.Parse(json.Text).GetProperty("data").GetString()?.Length == size,
                            ResourceContentKind.Text => result.Contents[0] is TextResourceContents text && text.Text.Length == size,
                            ResourceContentKind.Binary => result.Contents[0] is BlobResourceContents blob &&
                                blob.DecodedData.Length == size,
                            _ => false
                        };
                        if (!contentMatches)
                            throw new InvalidOperationException("The direct MCP resource adapter returned an unexpected result.");
                    }, plan, catalogSize, telemetryMode);
            }
        }

        static Dictionary<string, JsonElement> ToolArguments(int size) => new(StringComparer.Ordinal)
        {
            ["input"] = JsonSerializer.SerializeToElement("benchmark nested arguments"),
            ["result_bytes"] = JsonSerializer.SerializeToElement(size),
            ["nested"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["group"] = "north",
                ["values"] = NestedValues,
                ["source"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = "portia",
                    ["revision"] = 7
                }
            })
        };

        static JsonRpcRequest AdapterRequest(string method) => new()
        {
            Id = new RequestId(Interlocked.Increment(ref AdapterRequestId)),
            Method = method
        };
    }

    static async Task WriteAdapterMeasuredAsync<TContext, TResult>(StreamWriter output,
        McpBenchmarkServer server, McpAdapterOnlySession adapter, string operation, int payloadBytes,
        Func<IServiceProvider, TContext> createContext, Func<TContext, ValueTask<TResult>> invoke,
        Action<TResult> validate, CampaignPlan plan, int catalogSize, string telemetryMode)
    {
        var before = server.Telemetry.Snapshot();
        var measurement = await MeasureAdapterInvocationAsync(adapter, createContext, invoke, validate, plan);
        var after = server.Telemetry.Snapshot();
        await WriteMeasurementAsync(output, "adapter-only", catalogSize, telemetryMode, operation,
            payloadBytes, measurement, after.Difference(before));
    }

    static async Task<Measurement> MeasureAdapterInvocationAsync<TContext, TResult>(
        McpAdapterOnlySession adapter, Func<IServiceProvider, TContext> createContext,
        Func<TContext, ValueTask<TResult>> invoke, Action<TResult> validate, CampaignPlan plan)
    {
        var raw = new double[plan.Samples];
        for (var index = 0; index < plan.Warmups; index++)
            _ = await InvokeOnceAsync(measureLatency: false, validateResult: true).ConfigureAwait(false);

        for (var index = 0; index < raw.Length; index++)
        {
            var sample = await InvokeOnceAsync(measureLatency: true, validateResult: true).ConfigureAwait(false);
            raw[index] = sample * 1_000_000d / Stopwatch.Frequency;
        }

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var throughputStarted = Stopwatch.GetTimestamp();
        for (var index = 0; index < plan.ThroughputIterations; index++)
            _ = await InvokeOnceAsync(measureLatency: false, validateResult: false).ConfigureAwait(false);
        var throughputElapsed = Stopwatch.GetElapsedTime(throughputStarted);
        var throughputAllocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;

        return Measurement.From(raw, plan.ThroughputIterations, throughputElapsed.TotalSeconds, throughputAllocated);

        async ValueTask<long> InvokeOnceAsync(bool measureLatency, bool validateResult)
        {
            var scope = adapter.Server.ServerOptions.ScopeRequests
                ? adapter.Services.GetRequiredService<IServiceScopeFactory>().CreateScope()
                : null;
            try
            {
                var context = createContext(scope?.ServiceProvider ?? adapter.Services);
                if (!measureLatency)
                {
                    var unmeasuredResult = await invoke(context).ConfigureAwait(false);
                    if (validateResult)
                        validate(unmeasuredResult);
                    return 0;
                }

                var started = Stopwatch.GetTimestamp();
                var result = await invoke(context).ConfigureAwait(false);
                var elapsedTicks = Stopwatch.GetTimestamp() - started;
                if (validateResult)
                    validate(result);
                return elapsedTicks;
            }
            finally
            {
                scope?.Dispose();
            }
        }
    }

    static Task SendDirectAsync(IRequestBus bus, BenchmarkRequest request)
    {
        var result = bus.SendAsync(request, Actor);
        return result.IsCompletedSuccessfully
            ? ValidateDirectResult(result.Result)
            : AwaitAsync(result);

        static Task ValidateDirectResult(Result<BenchmarkOutput> response)
        {
            if (!response.IsSuccess)
                throw new InvalidOperationException("Direct request-bus control did not succeed.");
            return Task.CompletedTask;
        }

        static async Task AwaitAsync(ValueTask<Result<BenchmarkOutput>> pending)
        {
            var response = await pending.ConfigureAwait(false);
            if (!response.IsSuccess)
                throw new InvalidOperationException("Direct request-bus control did not succeed.");
        }
    }

    static async Task WriteMeasuredAsync(StreamWriter output, McpBenchmarkServer? server, string transport,
        int catalogSize, string telemetryMode, string operation, int payloadBytes, Func<Task> action,
        CampaignPlan plan)
    {
        var before = server?.Telemetry.Snapshot();
        var measurement = await MeasureAsync(action, plan);
        var after = server?.Telemetry.Snapshot();
        await WriteMeasurementAsync(output, transport, catalogSize, telemetryMode, operation, payloadBytes,
            measurement, before is not null && after is not null ? after.Difference(before) : null);
    }

    static async Task<Measurement> MeasureAsync(Func<Task> action, CampaignPlan plan)
    {
        for (var index = 0; index < plan.Warmups; index++)
            await action().ConfigureAwait(false);

        var raw = new double[plan.Samples];
        for (var index = 0; index < raw.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            await action().ConfigureAwait(false);
            raw[index] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }

        var allocationStart = GC.GetTotalAllocatedBytes(precise: false);
        var batchStarted = Stopwatch.GetTimestamp();
        for (var index = 0; index < plan.ThroughputIterations; index++)
            await action().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(batchStarted);
        var allocated = GC.GetTotalAllocatedBytes(precise: false) - allocationStart;
        return Measurement.From(raw, plan.ThroughputIterations, elapsed.TotalSeconds, allocated);
    }

    static async Task WriteMeasurementAsync(StreamWriter output, string transport, int catalogSize,
        string telemetryMode, string operation, int payloadBytes, Measurement measurement,
        TelemetrySnapshot? telemetry = null)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            recordType = "measurement",
            transport,
            catalogSize,
            telemetryMode,
            operation,
            payloadBytes,
            sampleCount = measurement.RawLatencyMicroseconds.Count,
            p50Microseconds = measurement.P50Microseconds,
            p95Microseconds = measurement.P95Microseconds,
            meanMicroseconds = measurement.MeanMicroseconds,
            throughputOperationsPerSecond = measurement.ThroughputOperationsPerSecond,
            processAllocatedBytesPerOperation = measurement.AllocatedBytesPerOperation,
            throughputBatchIterations = measurement.ThroughputIterations,
            throughputBatchElapsedSeconds = measurement.ThroughputElapsedSeconds,
            throughputBatchAllocatedBytes = measurement.ThroughputAllocatedBytes,
            rawLatencyMicroseconds = measurement.RawLatencyMicroseconds,
            telemetryCounts = telemetry
        }));
    }

    static async Task<Dictionary<string, object?>> MetadataAsync(string mode, CampaignPlan plan)
    {
        var root = FindRepositoryRoot();
        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,
            "bench/Portia.Benchmarks/obj/project.assets.json")).ConfigureAwait(false));
        var dependencies = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "package")
            .ToDictionary(library => library.Name[..library.Name.LastIndexOf('/')],
                library => library.Name[(library.Name.LastIndexOf('/') + 1)..], StringComparer.Ordinal);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["recordType"] = "metadata",
            ["capturedUtc"] = DateTimeOffset.UtcNow,
            ["runMode"] = mode,
            ["sourceCommit"] = await CommandOutputAsync("git", root, "rev-parse", "HEAD"),
            ["sourceTreeStatus"] = await CommandOutputAsync("git", root, "status", "--porcelain"),
            ["dotnetSdk"] = await CommandOutputAsync("dotnet", root, "--version"),
            ["runtime"] = Environment.Version.ToString(),
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["operatingSystem"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processorCount"] = Environment.ProcessorCount,
            ["serverGc"] = System.Runtime.GCSettings.IsServerGC,
            ["buildConfiguration"] = "Release",
            ["targetFramework"] = "net11.0",
            ["culture"] = CultureInfo.CurrentCulture.Name,
            ["dependencies"] = dependencies,
            ["benchmarkRunner"] = "Portia in-repository MCP protocol qualification",
            ["plan"] = plan,
            ["workload"] = new
            {
                transports = new[] { "Streamable HTTP over loopback Kestrel", "SDK-controlled stdio child process" },
                catalogSizes = CatalogSizes,
                telemetryModes = TelemetryModes,
                resultSizesBytes = PayloadSizes,
                nestedToolArguments = true,
                resourceFormats = new[] { "application/json", "text/plain", "application/octet-stream" },
                resourceFormatCoverage = "All formats are measured at catalog sizes 100 and 1000; size 1 uses JSON.",
                prompts = "Required size argument rendered to one text message.",
                controls = new[] { "IRequestBus.SendAsync", "generated Portia REST POST" },
                exporterWait = "No exporter is configured. Activity and metric listeners consume synchronously; logging uses an in-memory provider."
            },
            ["methodology"] = new
            {
                startup = "Builder composition, service provider creation, hosted validators, and Kestrel start.",
                httpNegotiation = "Official MCP SDK client creation and protocol initialize after server startup.",
                stdioStartupNegotiation = "SDK stdio transport combines child-process launch and initialize; reported as one measurement.",
                discovery = "Tool/resource-template/prompt listing measured independently before warmed primitive operations.",
                latency = "Sequential wall-clock operation samples; p50, p95, mean and every raw sample are retained.",
                allocations = "Process-wide managed bytes during the separate throughput batch divided by operations; HTTP includes client and in-process server; stdio measures the parent client process.",
                adapterDelta = "MCP measurements and direct/REST controls are separate rows; derived paired differences include client and transport cost.",
                adapterOnly = "The adapter-only rows call the registered Portia tool InvokeAsync method or the registered resource-read/prompt-get handler from the host's MCP server options. The MCP server dispatch pipeline, JSON-RPC serialization, client and transport are excluded. RequestContext and configured per-request service scope are created for every call before the latency timer starts. P50/P95/mean cover only the direct Portia adapter handler invocation; result validation runs after the latency timer. Throughput and process allocations cover the repeated direct-call loop including request-context and scope setup, but not result validation, protocol dispatch, client or transport.",
                exporterWait = false
            }
        };
    }

    static async Task<string> CommandOutputAsync(string command, string root, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {command} for benchmark metadata.");
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Could not read {command} benchmark metadata: "
                + await process.StandardError.ReadToEndAsync().ConfigureAwait(false));
        return output.Trim();
    }

    static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Portia.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Run the MCP qualification from within the Portia repository.");
    }

    internal static PortiaBuilder RegisterCatalog(PortiaBuilder application, int catalogSize,
        bool includeFixedLookupResource = false)
    {
        for (var index = 0; index < catalogSize; index++)
        {
            var resourceIndex = index;
            var template = $"benchmark://resource/{resourceIndex:D4}/{{size}}";
            application.AddMcpResource<BenchmarkRequest, BenchmarkOutput>(template,
                arguments => BenchmarkRequest.ForSize(ParseSize(arguments)), options =>
                {
                    options.Public();
                    options.Named($"resource-{resourceIndex:D4}");
                    switch (ResourceFormat.ForIndex(resourceIndex).Kind)
                    {
                        case ResourceContentKind.Json:
                            options.AsJson();
                            break;
                        case ResourceContentKind.Text:
                            options.AsText("text/plain", static result => result.Data);
                            break;
                        case ResourceContentKind.Binary:
                            options.AsBinary("application/octet-stream", static result => Encoding.UTF8.GetBytes(result.Data));
                            break;
                        default:
                            throw new InvalidOperationException("Unknown MCP resource format.");
                    }
                });
            application.AddMcpPrompt<BenchmarkRequest, BenchmarkOutput>($"benchmark_prompt_{resourceIndex:D4}",
                arguments => BenchmarkRequest.ForSize(ParseSize(arguments)),
                static result => [new McpPromptMessage("user", result.Data)], options =>
                {
                    options.Public();
                    options.Required("size", "Result size in bytes.");
                });
        }

        if (includeFixedLookupResource)
            application.AddMcpResource<BenchmarkRequest, BenchmarkOutput>("benchmark://fixed/lookup",
                _ => BenchmarkRequest.ForSize(4096), options =>
                {
                    options.Public();
                    options.Named("fixed-lookup-resource");
                    options.AsJson();
                });

        return BenchmarkToolCatalog.Register(application, catalogSize);

        static int ParseSize(IReadOnlyDictionary<string, string> arguments) =>
            int.TryParse(arguments["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                && PayloadSizes.Contains(size)
                ? size
                : throw new ArgumentException("Unsupported MCP benchmark result size.", nameof(arguments));
    }

    static void ConfigureLogging(ILoggingBuilder logging, string mode, BenchmarkTelemetry telemetry)
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(LogLevel.Debug);
        if (mode == "logging")
            logging.AddProvider(telemetry.LoggerProvider);
    }

    sealed record CampaignPlan(int Warmups, int Samples, int ThroughputIterations);

    sealed record Measurement(IReadOnlyList<double> RawLatencyMicroseconds, double P50Microseconds,
        double P95Microseconds, double MeanMicroseconds, double? ThroughputOperationsPerSecond,
        double? AllocatedBytesPerOperation, int ThroughputIterations, double? ThroughputElapsedSeconds,
        long? ThroughputAllocatedBytes)
    {
        internal static Measurement Single(double milliseconds)
        {
            var microseconds = milliseconds * 1000;
            return new Measurement([microseconds], microseconds, microseconds, microseconds,
                null, null, 0, null, null);
        }

        internal static Measurement From(double[] raw, int iterations, double seconds, long allocated)
        {
            var ordered = raw.Order().ToArray();
            var p50 = Quantile(ordered, 0.50);
            var p95 = Quantile(ordered, 0.95);
            return new Measurement(raw, p50, p95, raw.Average(), iterations / seconds,
                (double)allocated / iterations, iterations, seconds, allocated);
        }

        static double Quantile(double[] ordered, double quantile) => ordered.Length == 0
            ? 0
            : ordered[(int)Math.Ceiling(quantile * ordered.Length) - 1];
    }

    internal enum ResourceContentKind { Json, Text, Binary }

    internal readonly record struct ResourceFormat(string Name, ResourceContentKind Kind)
    {
        internal static ResourceFormat ForIndex(int index) => (index % 3) switch
        {
            0 => new ResourceFormat("json", ResourceContentKind.Json),
            1 => new ResourceFormat("text", ResourceContentKind.Text),
            _ => new ResourceFormat("binary", ResourceContentKind.Binary)
        };
    }
}
sealed class McpBenchmarkServer : IAsyncDisposable
{
    static readonly JsonSerializerOptions RestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    readonly WebApplication _application;
    readonly HttpClient _restClient;

    McpBenchmarkServer(WebApplication application, HttpClient restClient,
        BenchmarkTelemetry telemetry, double startupMilliseconds)
    {
        _application = application;
        _restClient = restClient;
        Telemetry = telemetry;
        StartupMilliseconds = startupMilliseconds;
        Endpoint = new Uri(new Uri(application.Urls.Single()), "/mcp");
        Bus = application.Services.GetRequiredService<IRequestBus>();
    }

    internal Uri Endpoint { get; }
    internal IRequestBus Bus { get; }
    internal BenchmarkTelemetry Telemetry { get; }
    internal double StartupMilliseconds { get; }

    internal McpAdapterOnlySession CreateAdapterOnlySession()
    {
        var options = _application.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var transport = new AdapterOnlyTransport();
        var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, _application.Services);
        return new McpAdapterOnlySession(server, _application.Services);
    }

    internal static async Task<McpBenchmarkServer> StartHttpAsync(int catalogSize, string telemetryMode,
        bool includeFixedLookupResource = false)
    {
        var started = Stopwatch.GetTimestamp();
        var telemetry = BenchmarkTelemetry.Attach(telemetryMode);
        WebApplication? app = null;
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            if (telemetryMode == "logging")
                builder.Logging.AddProvider(telemetry.LoggerProvider);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(serviceProvider => new BenchmarkState(
                telemetryMode == "logging", serviceProvider.GetRequiredService<ILoggerFactory>()));
            var application = builder.Services.AddPortia().AddHttp()
                .AddRequestHandler<BenchmarkRequestHandler>();
            application = McpProtocolQualification.RegisterCatalog(application, catalogSize,
                    includeFixedLookupResource)
                .AddMcpHttp();
            app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "mcp-benchmark")], "benchmark"));
                await next(context).ConfigureAwait(false);
            });
            _ = app.MapPortiaPost<BenchmarkRequest, BenchmarkOutput>("/rest").DisableAntiforgery();
            _ = app.MapPortiaMcp().AllowAnonymous();
            await app.StartAsync().ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var client = new HttpClient();
            return new McpBenchmarkServer(app, client, telemetry, elapsed);
        }
        catch
        {
            try
            {
                if (app is not null)
                    await app.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                telemetry.Dispose();
            }
            throw;
        }
    }

    internal async Task PostRestAsync(BenchmarkRequest request)
    {
        var requestBody = JsonSerializer.SerializeToUtf8Bytes(request, RestJson);
        using var content = new ByteArrayContent(requestBody);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await _restClient.PostAsync(new Uri(new Uri(_application.Urls.Single()), "/rest"), content)
            .ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The generated Portia REST control returned {(int)response.StatusCode}: "
                + Encoding.UTF8.GetString(bytes) + " Request body: " + Encoding.UTF8.GetString(requestBody));
        if (bytes.Length == 0)
            throw new InvalidOperationException("The generated Portia REST control returned no response body.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _restClient.Dispose();
        }
        finally
        {
            try
            {
                await _application.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await _application.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    Telemetry.Dispose();
                }
            }
        }
    }
}

sealed class McpAdapterOnlySession(McpServer server, IServiceProvider services) : IAsyncDisposable
{
    internal McpServer Server { get; } = server;
    internal IServiceProvider Services { get; } = services;

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

sealed class AdapterOnlyTransport : ITransport
{
    readonly Channel<JsonRpcMessage> _messages = Channel.CreateUnbounded<JsonRpcMessage>();

    public string? SessionId => null;
    public ChannelReader<JsonRpcMessage> MessageReader => _messages.Reader;

    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(
            "Adapter-only MCP measurements must not send protocol messages."));

    public ValueTask DisposeAsync()
    {
        _messages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

sealed class McpBenchmarkClient : IAsyncDisposable
{
    readonly McpClient _client;
    readonly HttpClient? _http;

    McpBenchmarkClient(McpClient client, HttpClient? http, double negotiationMilliseconds)
    {
        _client = client;
        _http = http;
        NegotiationMilliseconds = negotiationMilliseconds;
    }

    internal double NegotiationMilliseconds { get; }

    internal static async Task<McpBenchmarkClient> ConnectHttpAsync(Uri endpoint)
    {
        var http = new HttpClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp
        }, http, null, false);
        var started = Stopwatch.GetTimestamp();
        var client = await McpClient.CreateAsync(transport).ConfigureAwait(false);
        return new McpBenchmarkClient(client, http, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal static async Task<McpBenchmarkClient> ConnectStdioAsync(int catalogSize, string telemetryMode)
    {
        var assembly = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Cannot locate the benchmark assembly for the stdio child.");
        var options = new StdioClientTransportOptions
        {
            Command = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            Arguments = [assembly, "--mcp-stdio-server", catalogSize.ToString(CultureInfo.InvariantCulture), telemetryMode],
            Name = "Portia MCP performance qualification",
            WorkingDirectory = Path.GetDirectoryName(assembly),
            ShutdownTimeout = TimeSpan.FromSeconds(3)
        };
        var started = Stopwatch.GetTimestamp();
        var transport = new StdioClientTransport(options, NullLoggerFactory.Instance);
        var client = await McpClient.CreateAsync(transport).ConfigureAwait(false);
        return new McpBenchmarkClient(client, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal async Task VerifyPayloadsAsync(string transport, IReadOnlyList<int> payloadSizes)
    {
        foreach (var size in payloadSizes)
        {
            var toolResult = await _client.CallToolAsync("benchmark.tool.0000", ToolArguments(size))
                .ConfigureAwait(false);
            if (toolResult.IsError == true || toolResult.StructuredContent is not { } structured ||
                !structured.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("data", out var toolData) || toolData.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"The {transport} MCP tool call did not return structured payload data.");
            VerifyPayloadLength(toolData.GetString(), size, transport, "tool result");

            for (var formatIndex = 0; formatIndex < 3; formatIndex++)
                await VerifyResourcePayloadAsync(formatIndex, size, transport).ConfigureAwait(false);

            var promptResult = await _client.GetPromptAsync("benchmark_prompt_0000",
                new Dictionary<string, object?> { ["size"] = size.ToString(CultureInfo.InvariantCulture) })
                .ConfigureAwait(false);
            if (promptResult.Messages.Count != 1 || promptResult.Messages[0].Content is not TextContentBlock promptText)
                throw new InvalidOperationException($"The {transport} MCP prompt did not return one text message.");
            VerifyPayloadLength(promptText.Text, size, transport, "rendered prompt");
        }
    }

    async Task VerifyResourcePayloadAsync(int formatIndex, int size, string transport)
    {
        var result = await _client.ReadResourceAsync(
            $"benchmark://resource/{formatIndex:D4}/{size.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(false);
        if (result.Contents.Count != 1)
            throw new InvalidOperationException($"The {transport} MCP resource returned an unexpected number of contents.");

        switch (McpProtocolQualification.ResourceFormat.ForIndex(formatIndex).Kind)
        {
            case McpProtocolQualification.ResourceContentKind.Json when result.Contents[0] is TextResourceContents json &&
                                                                        json.MimeType == "application/json":
                using (var document = JsonDocument.Parse(json.Text))
                {
                    if (!document.RootElement.TryGetProperty("data", out var jsonData) ||
                        jsonData.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException($"The {transport} JSON resource omitted its string payload.");
                    VerifyPayloadLength(jsonData.GetString(), size, transport, "JSON resource data");
                }
                break;
            case McpProtocolQualification.ResourceContentKind.Text when result.Contents[0] is TextResourceContents text &&
                                                                        text.MimeType == "text/plain":
                VerifyPayloadLength(text.Text, size, transport, "text resource");
                break;
            case McpProtocolQualification.ResourceContentKind.Binary when result.Contents[0] is BlobResourceContents binary &&
                                                                          binary.MimeType == "application/octet-stream":
                if (binary.DecodedData.Length != size)
                    throw new InvalidOperationException($"The {transport} binary resource expected {size} bytes; " +
                        $"received {binary.DecodedData.Length}.");
                break;
            default:
                throw new InvalidOperationException($"The {transport} MCP resource returned an unexpected content type.");
        }
    }

    static void VerifyPayloadLength(string? payload, int expectedBytes, string transport, string operation)
    {
        if (payload is null)
            throw new InvalidOperationException($"The {transport} {operation} returned no payload.");
        var actualBytes = Encoding.UTF8.GetByteCount(payload);
        if (actualBytes != expectedBytes)
            throw new InvalidOperationException($"The {transport} {operation} expected {expectedBytes} bytes; " +
                $"received {actualBytes}.");
    }

    internal async Task DiscoverToolsAsync(int expectedCount)
    {
        var tools = await _client.ListToolsAsync().ConfigureAwait(false);
        if (tools.Count != expectedCount)
            throw new InvalidOperationException($"Expected {expectedCount} MCP tools; received {tools.Count}.");
    }

    internal async Task DiscoverResourcesAsync(int expectedCount)
    {
        var resources = await _client.ListResourceTemplatesAsync().ConfigureAwait(false);
        if (resources.Count != expectedCount)
            throw new InvalidOperationException($"Expected {expectedCount} MCP resource templates; received {resources.Count}.");
    }

    internal async Task DiscoverPromptsAsync(int expectedCount)
    {
        var prompts = await _client.ListPromptsAsync().ConfigureAwait(false);
        if (prompts.Count != expectedCount)
            throw new InvalidOperationException($"Expected {expectedCount} MCP prompts; received {prompts.Count}.");
    }

    internal async Task CallToolAsync(int size)
    {
        var result = await _client.CallToolAsync("benchmark.tool.0000", ToolArguments(size)).ConfigureAwait(false);
        if (result.IsError == true || result.StructuredContent is null)
            throw new InvalidOperationException("The MCP benchmark tool call did not return structured success.");
    }

    internal async Task ReadResourceAsync(int formatIndex, int size)
    {
        var result = await _client.ReadResourceAsync(
            $"benchmark://resource/{formatIndex:D4}/{size.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(false);
        if (result.Contents.Count != 1)
            throw new InvalidOperationException("The MCP benchmark resource returned an unexpected number of contents.");
        var format = formatIndex % 3;
        if (format == 2 && result.Contents[0] is not BlobResourceContents
            || format != 2 && result.Contents[0] is not TextResourceContents)
            throw new InvalidOperationException("The MCP benchmark resource content type did not match its media type.");
    }

    internal async Task GetPromptAsync(int size)
    {
        var result = await _client.GetPromptAsync("benchmark_prompt_0000",
            new Dictionary<string, object?> { ["size"] = size.ToString(CultureInfo.InvariantCulture) })
            .ConfigureAwait(false);
        if (result.Messages.Count != 1 || result.Messages[0].Content is not TextContentBlock)
            throw new InvalidOperationException("The MCP benchmark prompt did not render one text message.");
    }

    static Dictionary<string, object?> ToolArguments(int size) => new(StringComparer.Ordinal)
    {
        ["input"] = "benchmark nested arguments",
        ["result_bytes"] = size,
        ["nested"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["group"] = "north",
            ["values"] = new[] { 3, 5, 8 },
            ["source"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "portia",
                ["revision"] = 7
            }
        }
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _http?.Dispose();
        }
    }
}

sealed class BenchmarkState
{
    static readonly Action<ILogger, Exception?> RequestHandled = LoggerMessage.Define(
        LogLevel.Debug, new EventId(1, nameof(RequestHandled)), "MCP benchmark request handled.");

    readonly Dictionary<int, BenchmarkOutput> _outputs = new()
    {
        [256] = new BenchmarkOutput(new string('x', 256)),
        [4096] = new BenchmarkOutput(new string('x', 4096)),
        [65536] = new BenchmarkOutput(new string('x', 65536))
    };

    readonly ILogger _logger;

    internal BenchmarkState(bool loggingEnabled, ILoggerFactory loggerFactory)
    {
        LoggingEnabled = loggingEnabled;
        _logger = loggerFactory.CreateLogger("Cntryl.Portia.McpBenchmark");
    }

    internal bool LoggingEnabled { get; }
    internal BenchmarkOutput Output(int bytes) => _outputs.TryGetValue(bytes, out var output)
        ? output
        : throw new ArgumentOutOfRangeException(nameof(bytes));

    internal void LogRequest()
    {
        if (LoggingEnabled)
            RequestHandled(_logger, null);
    }
}

sealed class BenchmarkRequestHandler(BenchmarkState state)
    : IRequestHandler<BenchmarkRequest, BenchmarkOutput>
{
    public ValueTask<Result<BenchmarkOutput>> HandleAsync(IRequestContext<BenchmarkRequest> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.LogRequest();
        return ValueTask.FromResult(Result<BenchmarkOutput>.Success(state.Output(context.Request.ResultBytes)));
    }
}

static class BenchmarkToolHandlerCore
{
    internal static ValueTask<Result<BenchmarkOutput>> Handle(BenchmarkState state, int resultBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.LogRequest();
        return ValueTask.FromResult(Result<BenchmarkOutput>.Success(state.Output(resultBytes)));
    }
}

[Discriminator("benchmark.request")]
sealed record BenchmarkRequest(string Input, int ResultBytes, BenchmarkArguments Nested)
    : IRequest<BenchmarkOutput>, ICallable
{
    internal static BenchmarkRequest ForSize(int size) => new("benchmark nested arguments", size,
        new BenchmarkArguments("north", [3, 5, 8], new BenchmarkSource("portia", 7)));
}

abstract record BenchmarkToolRequest : IRequest<BenchmarkOutput>, ICallable
{
    public string Input { get; init; } = "benchmark nested arguments";
    public int ResultBytes { get; init; } = 256;
    public BenchmarkArguments Nested { get; init; } = new("north", [3, 5, 8], new BenchmarkSource("portia", 7));
}

sealed record BenchmarkArguments(string Group, int[] Values, BenchmarkSource Source);
sealed record BenchmarkSource(string Name, int Revision);
sealed record BenchmarkOutput(string Data);

sealed class BenchmarkTelemetry : IDisposable
{
    readonly ActivityListener? _activityListener;
    readonly MeterListener? _meterListener;
    readonly TelemetryLoggerProvider _loggerProvider = new();
    long _activities;
    long _recordedActivities;
    long _measurements;
    long _sampleSequence;

    BenchmarkTelemetry(string mode)
    {
        if (mode is "propagation" or "sampled-tracing")
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = static _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => SelectSample(mode, options.Parent.TraceFlags),
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => SelectSample(mode, ActivityTraceFlags.None),
                ActivityStopped = activity =>
                {
                    Interlocked.Increment(ref _activities);
                    if (activity.Recorded)
                        Interlocked.Increment(ref _recordedActivities);
                }
            };
            ActivitySource.AddActivityListener(_activityListener);
        }
        if (mode == "metrics")
        {
            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == PortiaTelemetry.SourceName)
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            _meterListener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref _measurements));
            _meterListener.SetMeasurementEventCallback<int>((_, _, _, _) => Interlocked.Increment(ref _measurements));
            _meterListener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref _measurements));
            _meterListener.Start();
        }
    }

    internal ILoggerProvider LoggerProvider => _loggerProvider;
    internal static BenchmarkTelemetry Attach(string mode) => new(mode);

    ActivitySamplingResult SelectSample(string mode, ActivityTraceFlags parentFlags)
    {
        if (mode == "propagation")
            return ActivitySamplingResult.PropagationData;
        if ((parentFlags & ActivityTraceFlags.Recorded) != 0 || Interlocked.Increment(ref _sampleSequence) % 10 == 0)
            return ActivitySamplingResult.AllDataAndRecorded;
        return ActivitySamplingResult.PropagationData;
    }

    internal TelemetrySnapshot Snapshot() => new(Interlocked.Read(ref _activities),
        Interlocked.Read(ref _recordedActivities), Interlocked.Read(ref _measurements), _loggerProvider.Count);

    public void Dispose()
    {
        _activityListener?.Dispose();
        _meterListener?.Dispose();
        _loggerProvider.Dispose();
    }
}

sealed record TelemetrySnapshot(long ActivityStopped, long RecordedActivities, long Measurements, long LogEntries)
{
    internal TelemetrySnapshot Difference(TelemetrySnapshot other) => new(
        ActivityStopped - other.ActivityStopped,
        RecordedActivities - other.RecordedActivities,
        Measurements - other.Measurements,
        LogEntries - other.LogEntries);
}

sealed class TelemetryLoggerProvider : ILoggerProvider
{
    long _count;
    internal long Count => Interlocked.Read(ref _count);
    public ILogger CreateLogger(string categoryName) => new TelemetryLogger(this);
    public void Dispose() { }

    sealed class TelemetryLogger(TelemetryLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                Interlocked.Increment(ref provider._count);
        }
    }
}

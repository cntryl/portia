using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Cntryl.Portia.Tests;

[Collection("MCP HTTP integration")]
public sealed class McpTelemetryQualificationTests
{
    [Theory]
    [InlineData("tool", false, false)]
    [InlineData("resource", false, false)]
    [InlineData("prompt", false, false)]
    [InlineData("tool", true, false)]
    [InlineData("resource", true, false)]
    [InlineData("prompt", true, false)]
    [InlineData("tool", true, true)]
    [InlineData("resource", true, true)]
    [InlineData("prompt", true, true)]
    public async Task ShouldExportBoundedCorrelatedSignalsForEveryPrimitive(string primitive, bool fails,
        bool cancellationException)
    {
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        var logs = new List<LogRecord>();
        var failure = new TelemetryFailure(cancellationException);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(failure);
        _ = builder.Services.AddOpenTelemetry().WithPortia()
            .WithTracing(tracing => tracing.SetSampler(new AlwaysOnSampler()).AddInMemoryExporter(activities))
            .WithMetrics(meter => meter.AddInMemoryExporter(metrics))
            .WithLogging(logging => logging.AddInMemoryExporter(logs));
        _ = builder.Services.AddPortia().AddRequestHandler<ReadTelemetryHandler>()
            .AddMcpTool<ReadTelemetry>()
            .AddMcpResource<ReadTelemetry, string>("telemetry://read/{fail}",
                values => new ReadTelemetry(bool.Parse(values["fail"])),
                options => { options.Authenticated(); options.AsText("text/plain", value => value); })
            .AddMcpPrompt<ReadTelemetry, string>("telemetry-read", values => new ReadTelemetry(bool.Parse(values["fail"])),
                value => [new McpPromptMessage("user", value)],
                options => { options.Authenticated(); options.Required("fail"); })
            .AddMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "telemetry-actor")], "test"));
            return next(context);
        });
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient());
        await using var client = await McpClient.CreateAsync(transport);
        var error = await Record.ExceptionAsync(async () =>
        {
            if (primitive == "tool")
            {
                var result = await client.CallToolAsync("telemetry.read", new Dictionary<string, object?> { ["fail"] = fails });
                Assert.Equal(fails, result.IsError == true);
                if (fails)
                    Assert.Null(result.StructuredContent);
            }
            else if (primitive == "resource")
                _ = await client.ReadResourceAsync($"telemetry://read/{fails}");
            else
                _ = await client.GetPromptAsync("telemetry-read", new Dictionary<string, object?> { ["fail"] = fails.ToString() });
        });
        if (fails && primitive != "tool")
        {
            Assert.NotNull(error);
            Assert.DoesNotContain("sensitive", error.Message, StringComparison.Ordinal);
        }
        else
            Assert.Null(error);

        Assert.True(app.Services.GetRequiredService<TracerProvider>().ForceFlush());
        Assert.True(app.Services.GetRequiredService<MeterProvider>().ForceFlush());
        Assert.True(app.Services.GetRequiredService<LoggerProvider>().ForceFlush());
        var process = Assert.Single(activities, activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        var execute = Assert.Single(activities, activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName);
        Assert.Equal(2, activities.Count);
        Assert.Equal(process.SpanId, execute.ParentSpanId);
        Assert.Equal(process.TraceId, execute.TraceId);
        foreach (var activity in activities)
        {
            Assert.Equal(fails ? "fault" : "success", activity.GetTagItem("portia.outcome"));
            Assert.DoesNotContain(activity.TagObjects, tag => Equals(tag.Value, "telemetry-actor")
                || Equals(tag.Value, $"telemetry://read/{fails}"));
        }
        var duration = Assert.Single(metrics, metric => metric.Name == "portia.request.duration");
        var durationCount = 0;
        foreach (ref readonly var point in duration.GetMetricPoints())
        {
            durationCount++;
            Assert.Equal(1, point.GetHistogramCount());
            var tags = new List<KeyValuePair<string, object?>>();
            foreach (var tag in point.Tags)
                tags.Add(tag);
            Assert.Equal(fails ? "fault" : "success",
                Assert.Single(tags, tag => tag.Key == "portia.outcome").Value);
            Assert.Equal(3, point.Tags.Count);
        }
        Assert.Equal(1, durationCount);
        if (fails)
        {
            var fault = Assert.Single(logs, log => log.EventId.Id == 1002);
            Assert.Equal(process.TraceId, fault.TraceId);
            Assert.Equal(process.SpanId, fault.SpanId);
            Assert.Same(failure.Exception, fault.Exception);
            Assert.Contains(metrics, metric => metric.Name == "portia.worker.failure");
        }
        else
            Assert.DoesNotContain(logs, log => log.EventId.Id == 1002);
    }

    public sealed class TelemetryFailure(bool cancellationException = false)
    {
        public Exception Exception { get; } = cancellationException
            ? new OperationCanceledException("sensitive unrequested cancellation")
            : new IOException("sensitive operator detail");
    }

    /// <summary>Reads data or raises an unexpected failure for transport telemetry qualification.</summary>
    [Discriminator("telemetry.read")]
    public sealed record ReadTelemetry(bool Fail) : IRequest<string>, ICallable;

    public sealed class ReadTelemetryHandler(TelemetryFailure failure) : IRequestHandler<ReadTelemetry, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<ReadTelemetry> context, CancellationToken ct) =>
            context.Request.Fail ? ValueTask.FromException<Result<string>>(failure.Exception)
                : ValueTask.FromResult(Result<string>.Success("ok"));
    }
}

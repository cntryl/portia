using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace Cntryl.Portia;

static class PrometheusQualification
{
    internal static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        using var provider = Sdk.CreateMeterProviderBuilder().AddMeter(PortiaTelemetry.SourceName)
            .AddView("portia.request.duration", new ExplicitBucketHistogramConfiguration
            { Boundaries = [0.001, 0.01, 0.1, 1, 10] })
            .AddView("portia.processor.lag", new ExplicitBucketHistogramConfiguration
            { Boundaries = [0.1, 1, 10, 60, 300] })
            .AddOtlpExporter((exporter, reader) =>
            {
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                exporter.Endpoint = new Uri("http://127.0.0.1:49090/api/v1/otlp/v1/metrics");
                reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1000;
            }).Build();
        var services = new ServiceCollection();
        var state = new MetricState();
        services.AddSingleton(state);
        services.AddPortia().AddRequestHandler<MetricHandler>();
        await using var application = services.BuildServiceProvider();
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:49090"), Timeout = TimeSpan.FromSeconds(5) };
        await using var pendingScope = application.CreateAsyncScope();
        var pending = pendingScope.ServiceProvider.GetRequiredService<IRequestBus>()
            .SendAsync(new MetricRequest("pending"), RequestActor.System).AsTask();
        await state.PendingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        PortiaTelemetry.RecordWorkload("dashboard.worker", "local", true);
        var outcomes = new[] { "success", "validation", "unauthorized", "forbidden", "not_found", "conflict", "canceled", "fault" };
        for (var phase = 0; phase < 2; phase++)
        {
            await using var scope = application.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
            foreach (var outcome in outcomes)
                for (var iteration = 0; iteration < 20; iteration++)
                {
                    using var cancellation = new CancellationTokenSource();
                    state.Cancellation = cancellation;
                    try
                    {
                        await bus.SendAsync(new MetricRequest(outcome), RequestActor.System, ct: cancellation.Token);
                    }
                    catch (OperationCanceledException) when (outcome == "canceled") { }
                    catch (InvalidOperationException) when (outcome == "fault") { }
                }
            PortiaTelemetry.ProcessorBatchFinished(PortiaTelemetry.StartTimestamp(), "dashboard.projector", "ProjectorRunner",
                "success", 3, DateTimeOffset.UtcNow.AddSeconds(-5));
            PortiaTelemetry.RecordRunnerFault("DashboardWorker", RunnerFaultStage.Execution, new InvalidOperationException("controlled fault"));
            PortiaTelemetry.RecordWorkerRestart("DashboardWorker", "execution");
            if (!provider.ForceFlush())
                throw new InvalidOperationException("Metric export did not flush.");
            await Task.Delay(1500);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "request_active_started.json"),
            await PollAsync(http, "sum(portia_request_active)", 1));
        await File.WriteAllTextAsync(Path.Combine(output, "workload_active_started.json"),
            await PollAsync(http, "sum(portia_workload_active)", 1));
        state.ReleasePending.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        PortiaTelemetry.RecordWorkload("dashboard.worker", "local", false);
        if (!provider.ForceFlush())
            throw new InvalidOperationException("Final metric export did not flush.");
        var result = await PollAsync(http, "sum(portia_request_duration_seconds_count)", 321);
        await File.WriteAllTextAsync(Path.Combine(output, "total.json"), result);
        var names = await http.GetStringAsync("/api/v1/label/__name__/values");
        await File.WriteAllTextAsync(Path.Combine(output, "names.json"), names);
        await File.WriteAllTextAsync(Path.Combine(output, "samples.json"), await QueryAsync(http, "{__name__=~\"portia_.*\"}"));
        var queries = new Dictionary<string, string>
        {
            ["throughput"] = "sum(rate(portia_request_duration_seconds_count[1m]))",
            ["latency_p95"] = "histogram_quantile(0.95, sum by (le) (rate(portia_request_duration_seconds_bucket[1m])))",
            ["expected_refusal"] = "sum(portia_request_duration_seconds_count{portia_outcome=~\"validation|unauthorized|forbidden|not_found|conflict\"})",
            ["canceled"] = "sum(portia_request_duration_seconds_count{portia_outcome=\"canceled\"})",
            ["fault"] = "sum(portia_request_duration_seconds_count{portia_outcome=\"fault\"})",
            ["request_active"] = "sum(portia_request_active)",
            ["workload_active"] = "sum(portia_workload_active)",
            ["worker_failure"] = "sum(portia_worker_failure_total)",
            ["worker_restart"] = "sum(portia_worker_restart_total)",
            ["commit_age_mean"] = "sum(portia_processor_lag_seconds_sum) / sum(portia_processor_lag_seconds_count)",
            ["commit_age_p95"] = "histogram_quantile(0.95, sum by (le) (rate(portia_processor_lag_seconds_bucket[1m])))"
        };
        foreach (var (name, query) in queries)
        {
            var json = await QueryAsync(http, query);
            RequireFiniteValue(json);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".json"), json);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "queries.json"), JsonSerializer.Serialize(queries));
        RequireValue(await QueryAsync(http, queries["expected_refusal"]), 200);
        RequireValue(await QueryAsync(http, queries["canceled"]), 40);
        RequireValue(await QueryAsync(http, queries["fault"]), 40);
        RequireValue(await QueryAsync(http, queries["request_active"]), 0);
        RequireValue(await QueryAsync(http, queries["workload_active"]), 0);
        RequireValue(await QueryAsync(http, queries["worker_failure"]), 2);
        RequireValue(await QueryAsync(http, queries["worker_restart"]), 2);
        Console.WriteLine("Prometheus 3.15.0 / OTLP 1.19.1: 321 actual requests, outcome counts, active gauges, worker counters and dashboard expressions verified.");
    }

    static Task<string> QueryAsync(HttpClient http, string expression) =>
        http.GetStringAsync("/api/v1/query?query=" + Uri.EscapeDataString(expression));

    static async Task<string> PollAsync(HttpClient http, string expression, double expected)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var json = await QueryAsync(http, expression);
            if (Value(json) == expected)
                return json;
            await Task.Delay(200);
        }
        throw new InvalidOperationException("Exported metrics did not reach the expected bounded workload count.");
    }

    static double? Value(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("status").GetString() != "success")
            throw new InvalidOperationException(json);
        var results = document.RootElement.GetProperty("data").GetProperty("result");
        return results.GetArrayLength() == 1
            ? double.Parse(results[0].GetProperty("value")[1].GetString()!, CultureInfo.InvariantCulture) : null;
    }
    static void RequireValue(string json, double expected)
    {
        if (Value(json) != expected)
            throw new InvalidOperationException($"Expected {expected}: {json}");
    }
    static void RequireFiniteValue(string json)
    {
        if (Value(json) is not { } value || !double.IsFinite(value))
            throw new InvalidOperationException(json);
    }

    internal sealed record MetricRequest(string Outcome) : IRequest;
    internal sealed class MetricState
    {
        internal CancellationTokenSource? Cancellation { get; set; }
        internal TaskCompletionSource PendingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleasePending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal sealed class MetricHandler(MetricState state) : IRequestHandler<MetricRequest>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<MetricRequest> context, CancellationToken ct)
        {
            var outcome = context.Request.Outcome;
            if (outcome == "pending")
            {
                state.PendingStarted.SetResult();
                await state.ReleasePending.Task.WaitAsync(ct);
                return Result.Success;
            }
            if (outcome == "canceled")
                state.Cancellation!.Cancel();
            ct.ThrowIfCancellationRequested();
            if (outcome == "fault")
                throw new InvalidOperationException("controlled handler fault");
            var result = outcome == "success" ? Result.Success : Result.Failure(new RequestError(outcome switch
            {
                "validation" => RequestErrorKind.Validation,
                "unauthorized" => RequestErrorKind.Unauthorized,
                "forbidden" => RequestErrorKind.Forbidden,
                "not_found" => RequestErrorKind.NotFound,
                "conflict" => RequestErrorKind.Conflict,
                _ => throw new InvalidOperationException("Unknown controlled outcome.")
            }, "expected refusal"));
            return result;
        }
    }
}

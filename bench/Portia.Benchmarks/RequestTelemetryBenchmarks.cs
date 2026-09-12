using System.Diagnostics;
using BenchmarkDotNet.Attributes;

namespace Cntryl.Portia;

#pragma warning disable CA1822 // BenchmarkDotNet requires benchmark methods to be instance methods.

/// <summary>Reports request telemetry helper cost.</summary>
[MemoryDiagnoser]
public class RequestTelemetryBenchmarks
{
    /// <summary>Control invocation for focused helper overhead.</summary>
    [Benchmark(Baseline = true)]
    public void Baseline()
    {
    }

    /// <summary>Measures the normal no-listener tracing path.</summary>
    [Benchmark]
    public void Disabled()
    {
        using var activity = PortiaTelemetry.StartExecute("registered.request", "local");
    }

    /// <summary>Measures bounded metrics without an exporter.</summary>
    [Benchmark]
    public void Metrics()
    {
        var started = PortiaTelemetry.StartTimestamp();
        PortiaTelemetry.RequestStarted("RegisteredRequest", "local");
        PortiaTelemetry.RequestFinished(started, "RegisteredRequest", "local", "success");
    }

    /// <summary>Measures the final-delivery counter without an exporter.</summary>
    [Benchmark]
    public void DeliveryCounter() =>
        PortiaTelemetry.RecordDelivery("registered.request", "queue", RequestDeliveryOutcome.Completed);
}

/// <summary>Reports sampled and linked request activity cost.</summary>
[MemoryDiagnoser]
public class SampledRequestTelemetryBenchmarks : IDisposable
{
    readonly QueueInvocation _invocation = new("queue://benchmark/request", 1) { MessagingSystem = "fitz" };
    readonly RequestTraceContext _propagated =
        new("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    ActivityListener? _listener;

    /// <summary>Enables complete activity data for the sampled-path measurements.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Removes the process-wide listener after the benchmark case.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Measures one sampled execute activity.</summary>
    [Benchmark(Baseline = true)]
    public void Sampled()
    {
        using var activity = PortiaTelemetry.StartExecute("registered.request", "local");
    }

    /// <summary>Measures one sampled process root with a producer link.</summary>
    [Benchmark]
    public void Linked()
    {
        using var activity = PortiaTelemetry.StartProcess("registered.request", _invocation, _propagated);
    }
}

#pragma warning restore CA1822

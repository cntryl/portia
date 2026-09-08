using BenchmarkDotNet.Attributes;

namespace Cntryl.Portia;

/// <summary>Reports request telemetry helper cost.</summary>
[MemoryDiagnoser]
public class RequestTelemetryBenchmarks
{
    /// <summary>Control invocation for focused helper overhead.</summary>
    [Benchmark(Baseline = true)]
    public static void Baseline() { }

    /// <summary>Measures the normal no-listener tracing path.</summary>
    [Benchmark]
    public static void Disabled()
    {
        using var activity = PortiaTelemetry.StartExecute("RegisteredRequest");
    }

    /// <summary>Measures bounded metrics without an exporter.</summary>
    [Benchmark]
    public static void Metrics()
    {
        var started = PortiaTelemetry.StartTimestamp();
        PortiaTelemetry.RequestStarted("RegisteredRequest", "local");
        PortiaTelemetry.RequestFinished(started, "RegisteredRequest", "local", "success");
    }
}

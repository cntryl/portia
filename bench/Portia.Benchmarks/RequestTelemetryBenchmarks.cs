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
        using var activity = PortiaTelemetry.StartExecute("RegisteredRequest");
    }

    /// <summary>Measures bounded metrics without an exporter.</summary>
    [Benchmark]
    public void Metrics()
    {
        var started = PortiaTelemetry.StartTimestamp();
        PortiaTelemetry.RequestStarted("RegisteredRequest", "local");
        PortiaTelemetry.RequestFinished(started, "RegisteredRequest", "local", "success");
    }
}

#pragma warning restore CA1822

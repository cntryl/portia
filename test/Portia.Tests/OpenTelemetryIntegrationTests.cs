using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Cntryl.Portia;

/// <summary>Proves the optional hosting package exports all three signals without contaminating core packages.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class OpenTelemetryIntegrationTests
{
    /// <summary>One idempotent call enables trace, metric, and correlated log export.</summary>
    [Fact]
    public void ShouldExportAllSignalsAndRemainIdempotent()
    {
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        var logs = new List<LogRecord>();
        var services = new ServiceCollection();
        _ = services.AddLogging();
        var telemetry = services.AddOpenTelemetry();
        _ = telemetry.WithPortia();
        var registeredServices = services.Count;
        _ = telemetry.WithPortia();
        Assert.Equal(registeredServices, services.Count);
        _ = telemetry
            .WithTracing(tracing => tracing.AddInMemoryExporter(activities))
            .WithMetrics(meter => meter.AddInMemoryExporter(metrics))
            .WithLogging(logging => logging.AddInMemoryExporter(logs));

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();
        var meterProvider = provider.GetRequiredService<MeterProvider>();
        var logger = provider.GetRequiredService<ILogger<OpenTelemetryIntegrationTests>>();
        var exception = new InvalidOperationException("operator diagnostic");

        using (var activity = PortiaTelemetry.StartExecute("test.request", "local"))
        {
            PortiaTelemetry.RecordRunnerFault("TestRunner", RunnerFaultStage.Execution, exception, logger);
            PortiaTelemetry.RecordOutcome(activity, true, null);
        }

        PortiaTelemetry.RecordDelivery("test.request", "local", RequestDeliveryOutcome.Completed);
        Assert.True(provider.GetRequiredService<TracerProvider>().ForceFlush());
        Assert.True(meterProvider.ForceFlush());
        Assert.True(provider.GetRequiredService<LoggerProvider>().ForceFlush());

        var exportedActivity = Assert.Single(activities,
            item => item.OperationName == PortiaTelemetry.ExecuteActivityName);
        Assert.Contains(metrics, metric => metric.Name == "portia.request.delivery.count");
        var exportedLog = Assert.Single(logs, log => log.EventId.Id == 1002);
        Assert.Equal(exportedActivity.TraceId, exportedLog.TraceId);
        Assert.Equal(exportedActivity.SpanId, exportedLog.SpanId);
        Assert.Same(exception, exportedLog.Exception);
    }

    /// <summary>Only the opt-in package has a metadata reference to an OpenTelemetry assembly.</summary>
    [Fact]
    public void ShouldKeepOpenTelemetryReferencesOutOfCorePackages()
    {
        var directory = Path.GetDirectoryName(typeof(PortiaTelemetry).Assembly.Location)!;
        var packageAssemblies = new[]
        {
            "Portia.Abstractions", "Portia.AspNetCore", "Portia.Core", "Portia.DependencyInjection", "Portia.Fitz",
            "Portia.Jwt", "Portia.Telemetry", "Portia.Testing"
        };

        var referencing = packageAssemblies.Where(name => ReferencesOpenTelemetry(Path.Combine(directory,
            name + ".dll"))).ToArray();

        Assert.Equal(["Portia.Telemetry"], referencing);
    }

    static bool ReferencesOpenTelemetry(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .Any(name => name.StartsWith("OpenTelemetry", StringComparison.Ordinal));
    }
}

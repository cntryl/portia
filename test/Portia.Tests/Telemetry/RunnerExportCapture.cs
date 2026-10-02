using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Cntryl.Portia;

sealed class RunnerExportCapture : IDisposable
{
    internal const string Parent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
    readonly ServiceProvider _provider;
    public LockedExportCollection<Activity> Activities { get; } = [];
    public LockedExportCollection<LogRecord> Logs { get; } = [];

    public RunnerExportCapture()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.Configure<OpenTelemetryLoggerOptions>(options => options.IncludeFormattedMessage = true);
        services.AddOpenTelemetry().WithPortia()
            .WithTracing(tracing => tracing.AddInMemoryExporter(Activities))
            .WithLogging(logging => logging.AddInMemoryExporter(Logs));
        _provider = services.BuildServiceProvider();
        _ = _provider.GetRequiredService<TracerProvider>();
    }

    public ILogger<T> Logger<T>() => _provider.GetRequiredService<ILogger<T>>();
    public void Flush()
    {
        Assert.True(_provider.GetRequiredService<TracerProvider>().ForceFlush());
        Assert.True(_provider.GetRequiredService<LoggerProvider>().ForceFlush());
    }
    public void Dispose() => _provider.Dispose();

    public void AssertCorrelated(int expectedEventId, LogLevel expectedLevel, int expectedDeliveries, int? expectedExecutions = null)
    {
        Flush();
        var processes = Activities.Where(activity => activity.OperationName == PortiaTelemetry.ProcessActivityName).ToArray();
        Assert.Equal(expectedDeliveries, processes.Length);
        Assert.All(processes, process => Assert.Equal(default, process.ParentSpanId));
        Assert.Equal(expectedDeliveries, processes.Select(activity => activity.TraceId).Distinct().Count());
        var logs = Logs.Where(log => log.EventId.Id == expectedEventId).ToArray();
        Assert.Equal(expectedDeliveries, logs.Length);
        foreach (var log in logs)
        {
            Assert.Equal(expectedLevel, log.LogLevel);
            Assert.Contains(processes, process => process.TraceId == log.TraceId && process.SpanId == log.SpanId);
            Assert.Contains(log.Attributes!, attribute => attribute.Key == "{OriginalFormat}");
            Assert.NotNull(log.FormattedMessage);
            if (expectedEventId == 1002)
            {
                Assert.NotNull(log.Exception);
                var process = processes.Single(item => item.TraceId == log.TraceId && item.SpanId == log.SpanId);
                Assert.Single(process.Events, item => item.Name == "exception");
            }
        }
        Assert.Equal(expectedDeliveries + (expectedExecutions ?? expectedDeliveries), Activities.Count);
    }
}

sealed class LockedExportCollection<T> : ICollection<T>
{
    readonly List<T> _items = [];
    readonly object _gate = new();
    public int Count { get { lock (_gate) return _items.Count; } }
    public bool IsReadOnly => false;
    public void Add(T item) { lock (_gate) _items.Add(item); }
    public void Clear() { lock (_gate) _items.Clear(); }
    public bool Contains(T item) { lock (_gate) return _items.Contains(item); }
    public void CopyTo(T[] array, int arrayIndex) { lock (_gate) _items.CopyTo(array, arrayIndex); }
    public bool Remove(T item) { lock (_gate) return _items.Remove(item); }
    public IEnumerator<T> GetEnumerator()
    {
        lock (_gate)
            return ((IEnumerable<T>)_items.ToArray()).GetEnumerator();
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

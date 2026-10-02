using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Cntryl.Portia;

// Application-owned exporters in the controlled child process, never protocol stdout.
sealed class QualificationTelemetry(string directory)
{
    readonly ExportBuffer<Activity> _activities = new();
    readonly ExportBuffer<Metric> _metrics = new();
    readonly ExportBuffer<LogRecord> _logs = new();

    internal void Configure(IServiceCollection services) => services.AddOpenTelemetry().WithPortia()
        .WithTracing(tracing => tracing.SetSampler(new AlwaysOnSampler()).AddInMemoryExporter(_activities))
        .WithMetrics(meter => meter.AddInMemoryExporter(_metrics))
        .WithLogging(logging => logging.AddInMemoryExporter(_logs));

    internal async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        var tracer = services.GetRequiredService<TracerProvider>();
        var meter = services.GetRequiredService<MeterProvider>();
        var logger = services.GetRequiredService<LoggerProvider>();
        Directory.CreateDirectory(directory);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (!tracer.ForceFlush() || !meter.ForceFlush() || !logger.ForceFlush())
                throw new InvalidOperationException("Qualification exporter could not flush.");
            await WriteAsync("spans.tsv", _activities.Select(activity => string.Join('\t', activity.OperationName,
                activity.TraceId, activity.SpanId, activity.ParentSpanId, activity.GetTagItem("portia.outcome"),
                activity.GetTagItem("portia.transport.name"))), ct);
            await WriteAsync("logs.tsv", _logs.Where(log => log.EventId.Id == 1002).Select(log =>
                string.Join('\t', log.EventId.Id, log.TraceId, log.SpanId, log.Exception?.GetType().FullName)), ct);
            var measurements = new List<string>();
            foreach (var metric in _metrics)
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    if (metric.Name is not ("portia.request.duration" or "portia.worker.failure"))
                        continue;
                    var count = metric.Name == "portia.request.duration" ? point.GetHistogramCount() : point.GetSumLong();
                    var tags = new List<string>();
                    foreach (var tag in point.Tags)
                        tags.Add(tag.Key + "=" + tag.Value);
                    measurements.Add(string.Join('\t', metric.Name, count.ToString(CultureInfo.InvariantCulture),
                        string.Join(',', tags)));
                }
            await WriteAsync("metrics.tsv", measurements, ct);
        }
    }

    async Task WriteAsync(string name, IEnumerable<string> lines, CancellationToken ct)
    {
        var target = Path.Combine(directory, name);
        var temporary = target + ".tmp";
        await File.WriteAllLinesAsync(temporary, lines, ct);
        File.Move(temporary, target, true);
    }

    sealed class ExportBuffer<T> : ICollection<T>
    {
        readonly ConcurrentQueue<T> _items = new();
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public void Add(T item) => _items.Enqueue(item);
        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Contains(T item) => _items.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public void Clear() => _items.Clear();
        public bool Remove(T item) => throw new NotSupportedException();
    }
}

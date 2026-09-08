using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Provides Portia's dependency-free tracing, metrics, and structured-logging contract.
/// Applications subscribe to the exposed <see cref="ActivitySource" /> and <see cref="Meter" />
/// with their chosen diagnostics SDK. Portia does not configure sampling or exporters.
/// </summary>
public static partial class PortiaTelemetry
{
    /// <summary>Gets the stable name shared by Portia's activity source and meter.</summary>
    public const string SourceName = "Cntryl.Portia";
    /// <summary>Gets the version of the emitted span and instrument contract.</summary>
    public const string Version = "1.0.0";
    /// <summary>Gets the static name used for outbound producer activities.</summary>
    public const string SendActivityName = "portia.request.send";
    /// <summary>Gets the static name used for inbound consumer activities.</summary>
    public const string ProcessActivityName = "portia.request.process";
    /// <summary>Gets the static name used for request-handler execution activities.</summary>
    public const string ExecuteActivityName = "portia.request.execute";
    /// <summary>Gets the activity source applications subscribe to with <see cref="SourceName" />.</summary>
    public static ActivitySource ActivitySource { get; } = new(SourceName, Version);
    /// <summary>Gets the meter applications subscribe to with <see cref="SourceName" />.</summary>
    public static Meter Meter { get; } = new(SourceName, Version);

    static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("portia.request.duration", "s");
    static readonly UpDownCounter<long> RequestActive = Meter.CreateUpDownCounter<long>("portia.request.active", "{request}");
    static readonly Histogram<double> AuthorizationDuration = Meter.CreateHistogram<double>("portia.authorization.duration", "s");
    static readonly Histogram<double> TransportDuration = Meter.CreateHistogram<double>("portia.transport.operation.duration", "s");
    static readonly Counter<long> InvalidTraceContext = Meter.CreateCounter<long>("portia.transport.trace_context.invalid", "{request}");
    static readonly Histogram<double> AggregateDuration = Meter.CreateHistogram<double>("portia.aggregate.operation.duration", "s");
    static readonly Counter<long> AggregateEvents = Meter.CreateCounter<long>("portia.aggregate.event.count", "{event}");
    static readonly Histogram<double> EventStoreDuration = Meter.CreateHistogram<double>("portia.event_store.operation.duration", "s");
    static readonly Counter<long> EventStoreEvents = Meter.CreateCounter<long>("portia.event_store.event.count", "{event}");
    static readonly Histogram<double> ProcessorDuration = Meter.CreateHistogram<double>("portia.processor.batch.duration", "s");
    static readonly Counter<long> ProcessorEvents = Meter.CreateCounter<long>("portia.processor.event.count", "{event}");
    static readonly Histogram<double> ProcessorLag = Meter.CreateHistogram<double>("portia.processor.lag", "s");
    static readonly UpDownCounter<long> WorkloadActive = Meter.CreateUpDownCounter<long>("portia.workload.active", "{request}");
    static readonly Counter<long> WorkerFailure = Meter.CreateCounter<long>("portia.worker.failure", "{request}");
    static readonly Counter<long> WorkerRestart = Meter.CreateCounter<long>("portia.worker.restart", "{request}");
    static readonly UpDownCounter<long> FleetAssignmentActive = Meter.CreateUpDownCounter<long>("portia.fleet.assignment.active", "{assignment}");

    /// <summary>Starts an internal activity for one request-handler execution.</summary>
    /// <param name="requestType">The startup-bounded registered request type name.</param>
    /// <returns>The started activity, or <see langword="null" /> when no listener samples it.</returns>
    public static Activity? StartExecute(string requestType)
    {
        var activity = ActivitySource.StartActivity(ExecuteActivityName, ActivityKind.Internal);
        _ = activity?.SetTag("request.type", requestType);
        return activity;
    }

    /// <summary>Starts a producer activity for one outbound transport operation.</summary>
    /// <param name="requestType">The startup-bounded registered request type name.</param>
    /// <param name="transport">The stable transport identifier.</param>
    /// <returns>The started activity, or <see langword="null" /> when no listener samples it.</returns>
    public static Activity? StartSend(string requestType, string transport)
    {
        var activity = ActivitySource.StartActivity(SendActivityName, ActivityKind.Producer);
        _ = activity?.SetTag("request.type", requestType);
        _ = activity?.SetTag("messaging.system", transport);
        return activity;
    }

    /// <summary>Starts a consumer activity for one inbound request delivery.</summary>
    /// <param name="requestType">The startup-bounded registered request type name.</param>
    /// <param name="transport">The stable transport identifier.</param>
    /// <param name="propagated">The optional W3C context received with the request.</param>
    /// <param name="linked"><see langword="true" /> to start a new trace linked to
    /// <paramref name="propagated" />; <see langword="false" /> to use it as the parent.</param>
    /// <returns>The started activity, or <see langword="null" /> when no listener samples it.</returns>
    /// <remarks>Invalid propagated context is ignored and increments
    /// <c>portia.transport.trace_context.invalid</c>. Scheduled delivery should set
    /// <paramref name="linked" /> to prevent recurring work from extending one trace indefinitely.</remarks>
    public static Activity? StartProcess(string requestType, string transport, RequestTraceContext? propagated, bool linked)
    {
        ActivityContext parent = default;
        if (propagated is { } context && !context.TryParse(out parent))
            InvalidTraceContext.Add(1, new KeyValuePair<string, object?>("transport", transport));
        var links = linked && parent != default ? new[] { new ActivityLink(parent) } : null;
        var activity = ActivitySource.StartActivity(ProcessActivityName, ActivityKind.Consumer,
            linked ? default : parent, tags: null, links);
        _ = activity?.SetTag("request.type", requestType);
        _ = activity?.SetTag("messaging.system", transport);
        return activity;
    }

    /// <summary>Captures the current activity's W3C trace-parent and trace-state fields.</summary>
    /// <returns>The propagation-safe context, or <see langword="null" /> when no activity is current.</returns>
    /// <remarks>Baggage, credentials, claims, and application payload data are never captured.</remarks>
    public static RequestTraceContext? CaptureTraceContext() =>
        Activity.Current is { Id: { } id } current ? new(id, current.TraceStateString) : null;
    /// <summary>Captures a monotonic timestamp suitable for the duration-recording methods.</summary>
    /// <returns>A timestamp produced by <see cref="Stopwatch.GetTimestamp()" />.</returns>
    public static long StartTimestamp() => Stopwatch.GetTimestamp();
    static double Seconds(long started) => Stopwatch.GetElapsedTime(started).TotalSeconds;

    /// <summary>Increments the active-request instrument.</summary>
    /// <param name="requestType">The startup-bounded registered request type name.</param>
    /// <param name="transport">The stable transport identifier.</param>
    public static void RequestStarted(string requestType, string transport) => RequestActive.Add(1, new("request.type", requestType), new("transport", transport));
    /// <summary>Decrements the active-request instrument and records request duration.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="requestType">The startup-bounded registered request type name.</param>
    /// <param name="transport">The stable transport identifier.</param>
    /// <param name="outcome">A stable outcome returned by <see cref="Outcome" /> or another documented closed-set value.</param>
    public static void RequestFinished(long started, string requestType, string transport, string outcome)
    {
        RequestActive.Add(-1, new("request.type", requestType), new("transport", transport));
        RequestDuration.Record(Seconds(started), new("request.type", requestType), new("transport", transport), new("outcome", outcome));
    }
    /// <summary>Records the duration and outcome of one authorization policy or stage.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="policy">The startup-bounded policy or authorizer name.</param>
    /// <param name="stage">The stable authorization stage.</param>
    /// <param name="outcome">The stable authorization outcome.</param>
    public static void AuthorizationFinished(long started, string policy, string stage, string outcome) =>
        AuthorizationDuration.Record(Seconds(started), new("component", policy), new("stage", stage), new("outcome", outcome));
    /// <summary>Records the duration and outcome of one transport operation.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="transport">The stable transport identifier.</param>
    /// <param name="operation">The stable transport operation.</param>
    /// <param name="outcome">The stable operation outcome.</param>
    public static void TransportFinished(long started, string transport, string operation, string outcome) =>
        TransportDuration.Record(Seconds(started), new("transport", transport), new("operation", operation), new("outcome", outcome));
    /// <summary>Records an aggregate operation and, when nonzero, its event count.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="operation">The stable aggregate operation.</param>
    /// <param name="outcome">The stable operation outcome.</param>
    /// <param name="eventCount">The number of events handled; zero suppresses the count instrument.</param>
    public static void AggregateFinished(long started, string operation, string outcome, int eventCount = 0)
    {
        AggregateDuration.Record(Seconds(started), new("operation", operation), new("outcome", outcome));
        if (eventCount > 0) AggregateEvents.Add(eventCount, new KeyValuePair<string, object?>("operation", operation));
    }
    /// <summary>Records an event-store operation and, when nonzero, its event count.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="operation">The stable event-store operation.</param>
    /// <param name="scope">The stable stream or pattern scope.</param>
    /// <param name="outcome">The stable operation outcome.</param>
    /// <param name="eventCount">The number of events handled; zero suppresses the count instrument.</param>
    public static void EventStoreFinished(long started, string operation, string scope, string outcome, int eventCount = 0)
    {
        EventStoreDuration.Record(Seconds(started), new("operation", operation), new("scope", scope), new("outcome", outcome));
        if (eventCount > 0) EventStoreEvents.Add(eventCount, new("operation", operation), new("scope", scope));
    }
    /// <summary>Records a processor batch, its event count, and optional commit lag.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="component">The startup-bounded registered processor name.</param>
    /// <param name="runner">The stable processor-runner kind.</param>
    /// <param name="outcome">The stable batch outcome.</param>
    /// <param name="eventCount">The number of events in the batch.</param>
    /// <param name="lastOccurrence">The occurrence time of the last committed event, or
    /// <see langword="null" /> to omit lag. Negative lag is clamped to zero.</param>
    public static void ProcessorBatchFinished(long started, string component, string runner, string outcome, int eventCount, DateTimeOffset? lastOccurrence = null)
    {
        ProcessorDuration.Record(Seconds(started), new("component", component), new("runner", runner), new("outcome", outcome));
        if (eventCount > 0) ProcessorEvents.Add(eventCount, new("component", component), new("runner", runner));
        if (lastOccurrence is { } occurrence)
            ProcessorLag.Record(Math.Max(0, (DateTimeOffset.UtcNow - occurrence).TotalSeconds), new("component", component), new("runner", runner));
    }
    /// <summary>Records a balanced workload lifecycle transition and optional information log.</summary>
    /// <param name="component">The startup-bounded registered workload name.</param>
    /// <param name="scope">The stable workload scope.</param>
    /// <param name="active"><see langword="true" /> when acquired; <see langword="false" /> when released.</param>
    /// <param name="logger">An optional logger for the lifecycle transition.</param>
    public static void RecordWorkload(string component, string scope, bool active, ILogger? logger = null)
    {
        WorkloadActive.Add(active ? 1 : -1, new("component", component), new("scope", scope));
        if (logger is not null) LogWorkload(logger, component, scope, active);
    }
    /// <summary>Increments the worker-restart counter.</summary>
    /// <param name="runner">The stable runner name.</param>
    /// <param name="stage">The stable restart stage.</param>
    public static void RecordWorkerRestart(string runner, string stage) => WorkerRestart.Add(1, new("runner", runner), new("stage", stage));

    /// <summary>Records a stable outcome and error status on a sampled request activity.</summary>
    /// <param name="activity">The request activity, or <see langword="null" /> when unsampled.</param>
    /// <param name="isSuccess">Whether request handling succeeded.</param>
    /// <param name="error">The expected request error when handling failed.</param>
    public static void RecordOutcome(Activity? activity, bool isSuccess, RequestError? error)
    {
        if (activity is null) return;
        _ = activity.SetTag("outcome", Outcome(isSuccess, error));
        if (!isSuccess) _ = activity.SetStatus(ActivityStatusCode.Error);
    }
    /// <summary>Maps a request result to the stable outcome vocabulary.</summary>
    /// <param name="success">Whether the request succeeded.</param>
    /// <param name="error">The expected request error when unsuccessful.</param>
    /// <returns><c>success</c>, a known request-error kind, or <c>fault</c>.</returns>
    public static string Outcome(bool success, RequestError? error = null) => success ? "success" : error?.Kind switch
    {
        RequestErrorKind.Validation => "validation",
        RequestErrorKind.Unauthorized => "unauthorized",
        RequestErrorKind.Forbidden => "forbidden",
        RequestErrorKind.NotFound => "not_found",
        RequestErrorKind.Conflict => "conflict",
        _ => "fault",
    };

    /// <summary>Records a bounded background-fault metric and structured error log.</summary>
    /// <param name="runnerName">The stable runner name.</param>
    /// <param name="reason">A diagnostic reason used only to select a closed-set log stage.</param>
    /// <param name="exception">The unexpected exception, when available.</param>
    /// <param name="logger">An optional logger for the structured fault event.</param>
    /// <remarks>This method never starts an activity. When an activity is already current,
    /// <paramref name="exception" /> is attached to it as an exception event.</remarks>
    public static void RecordRunnerFault(string runnerName, string reason, Exception? exception = null, ILogger? logger = null)
    {
        WorkerFailure.Add(1, new("runner", runnerName), new("error.type", exception?.GetType().Name ?? "unexpected_termination"));
        if (logger is not null) LogRunnerFault(logger, runnerName, FaultStage(reason), exception?.GetType().Name ?? "unexpected_termination", exception);
        if (exception is not null && Activity.Current is { } activity) _ = activity.AddException(exception);
    }

    static string FaultStage(string reason)
    {
        return reason switch
        {
            var value when value.Contains("renew", StringComparison.OrdinalIgnoreCase) => "renewal",
            var value when value.Contains("cleanup", StringComparison.OrdinalIgnoreCase) || value.Contains("callback", StringComparison.OrdinalIgnoreCase) => "cleanup",
            var value when value.Contains("validation", StringComparison.OrdinalIgnoreCase) => "validation",
            var value when value.Contains("watch", StringComparison.OrdinalIgnoreCase) => "watch",
            var value when value.Contains("acquir", StringComparison.OrdinalIgnoreCase) || value.Contains("lease", StringComparison.OrdinalIgnoreCase) => "acquisition",
            var value when value.Contains("notification", StringComparison.OrdinalIgnoreCase) => "notification",
            var value when value.Contains("workload", StringComparison.OrdinalIgnoreCase) => "workload",
            _ => "execution",
        };
    }
    /// <summary>Records a fleet-assignment gauge transition and optional information log.</summary>
    /// <param name="workerId">The worker identity used only in the lifecycle log.</param>
    /// <param name="partition">The partition identity used only in the lifecycle log.</param>
    /// <param name="assigned"><see langword="true" /> when assigned; <see langword="false" /> when released.</param>
    /// <param name="logger">An optional logger for the lifecycle transition.</param>
    /// <remarks>Worker and partition identities are deliberately excluded from metric tags.</remarks>
    public static void RecordFleetAssignment(string workerId, string partition, bool assigned, ILogger? logger = null)
    {
        FleetAssignmentActive.Add(assigned ? 1 : -1, new KeyValuePair<string, object?>("scope", "partition"));
        if (logger is not null) LogFleetAssignment(logger, workerId, partition, assigned);
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Portia worker {WorkerId} partition {Partition} assigned: {Assigned}")]
    static partial void LogFleetAssignment(ILogger logger, string workerId, string partition, bool assigned);
    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Portia {RunnerName} fault at {Stage} ({ErrorType})")]
    static partial void LogRunnerFault(ILogger logger, string runnerName, string stage, string errorType, Exception? exception);
    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Portia workload {Component} ({Scope}) active: {Active}")]
    static partial void LogWorkload(ILogger logger, string component, string scope, bool active);
}

/// <summary>Represents the W3C trace fields Portia propagates across request transports.</summary>
/// <param name="TraceParent">The W3C <c>traceparent</c> field.</param>
/// <param name="TraceState">The optional W3C <c>tracestate</c> field.</param>
/// <remarks>This type intentionally excludes baggage and application security context.</remarks>
public sealed record RequestTraceContext(string TraceParent, string? TraceState = null)
{
    /// <summary>Attempts to parse these fields using the BCL W3C parser.</summary>
    /// <param name="context">Receives the parsed activity context when valid.</param>
    /// <returns><see langword="true" /> when the fields form a valid W3C activity context.</returns>
    public bool TryParse(out ActivityContext context) => ActivityContext.TryParse(TraceParent, TraceState, out context);
}

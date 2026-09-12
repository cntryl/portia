using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
///     Provides Portia's dependency-free tracing, metrics, and structured-logging contract.
///     Applications subscribe to the exposed <see cref="ActivitySource" /> and <see cref="Meter" />
///     with their chosen diagnostics SDK. Portia does not configure sampling or exporters.
/// </summary>
public static partial class PortiaTelemetry
{
    /// <summary>Gets the stable name shared by Portia's activity source and meter.</summary>
    public const string SourceName = "Cntryl.Portia";

    /// <summary>Gets the version of the emitted span and instrument contract.</summary>
    public const string Version = "2.0.0";

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

    // A collector given no advice uses its own default boundaries, which run from 5 to 10,000 —
    // built for milliseconds. These instruments record seconds, so without advice every ordinary
    // measurement falls in the first bucket and no percentile survives. The latency boundaries are
    // the ones OpenTelemetry's own semantic conventions recommend for a seconds-valued duration.
    static readonly InstrumentAdvice<double> LatencyBuckets = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
    };

    // Processing lag is a backlog, not a request latency: it is routinely minutes behind and the
    // interesting question is which order of magnitude, so it gets its own, much wider, scale.
    static readonly InstrumentAdvice<double> LagBuckets = new()
    {
        HistogramBucketBoundaries = [0.1, 0.5, 1, 5, 10, 30, 60, 300, 600, 1800, 3600]
    };

    static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("portia.request.duration", "s", null, null, LatencyBuckets);

    static readonly UpDownCounter<long> RequestActive =
        Meter.CreateUpDownCounter<long>("portia.request.active", "{request}");

    static readonly Counter<long> RequestDelivery =
        Meter.CreateCounter<long>("portia.request.delivery.count", "{delivery}");

    static readonly Histogram<double> AuthorizationDuration =
        Meter.CreateHistogram<double>("portia.authorization.duration", "s", null, null, LatencyBuckets);

    static readonly Histogram<double> TransportDuration =
        Meter.CreateHistogram<double>("portia.transport.operation.duration", "s", null, null, LatencyBuckets);

    static readonly Counter<long> InvalidTraceContext =
        Meter.CreateCounter<long>("portia.transport.trace_context.invalid", "{request}");

    static readonly Histogram<double> AggregateDuration =
        Meter.CreateHistogram<double>("portia.aggregate.operation.duration", "s", null, null, LatencyBuckets);

    static readonly Counter<long>
        AggregateEvents = Meter.CreateCounter<long>("portia.aggregate.event.count", "{event}");

    static readonly Histogram<double> EventStoreDuration =
        Meter.CreateHistogram<double>("portia.event_store.operation.duration", "s", null, null, LatencyBuckets);

    static readonly Counter<long> EventStoreEvents =
        Meter.CreateCounter<long>("portia.event_store.event.count", "{event}");

    static readonly Histogram<double> ProcessorDuration =
        Meter.CreateHistogram<double>("portia.processor.batch.duration", "s", null, null, LatencyBuckets);

    static readonly Counter<long>
        ProcessorEvents = Meter.CreateCounter<long>("portia.processor.event.count", "{event}");

    static readonly Histogram<double> ProcessorLag =
        Meter.CreateHistogram<double>("portia.processor.lag", "s", null, null, LagBuckets);

    static readonly UpDownCounter<long> WorkloadActive =
        Meter.CreateUpDownCounter<long>("portia.workload.active", "{workload}");

    static readonly Counter<long> WorkerFailure = Meter.CreateCounter<long>("portia.worker.failure", "{failure}");
    static readonly Counter<long> WorkerRestart = Meter.CreateCounter<long>("portia.worker.restart", "{restart}");

    static readonly UpDownCounter<long> FleetAssignmentActive =
        Meter.CreateUpDownCounter<long>("portia.fleet.assignment.active", "{assignment}");

    /// <summary>Starts an internal activity for one request-handler execution.</summary>
    /// <param name="requestName">The startup-bounded generated request discriminator.</param>
    /// <param name="transport">The stable transport shape.</param>
    /// <returns>The started activity, or <see langword="null" /> when no listener samples it.</returns>
    public static Activity? StartExecute(string requestName, string transport)
    {
        var activity = ActivitySource.StartActivity(ExecuteActivityName, ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
        {
            _ = activity.SetTag("portia.request.name", requestName);
            _ = activity.SetTag("portia.transport.name", transport);
        }

        return activity;
    }

    /// <summary>Starts a producer activity for one outbound transport operation.</summary>
    /// <param name="requestName">The startup-bounded generated request discriminator.</param>
    /// <param name="transport">The stable transport shape.</param>
    /// <param name="messagingSystem">The standard messaging system name when the adapter knows it.</param>
    /// <returns>The started activity, or <see langword="null" /> when no listener samples it.</returns>
    public static Activity? StartSend(string requestName, string transport, string? messagingSystem)
    {
        var activity = ActivitySource.StartActivity(SendActivityName, ActivityKind.Producer);
        if (activity?.IsAllDataRequested == true)
        {
            _ = activity.SetTag("portia.request.name", requestName);
            _ = activity.SetTag("portia.transport.name", transport);
            if (messagingSystem is not null)
            {
                _ = activity.SetTag("messaging.system", messagingSystem);
                _ = activity.SetTag("messaging.operation.type", "send");
            }
        }

        return activity;
    }

    /// <summary>Starts a consumer activity for one inbound request delivery.</summary>
    /// <param name="requestName">The startup-bounded generated request discriminator.</param>
    /// <param name="invocation">The receiving transport's bounded tracing facts.</param>
    /// <param name="propagated">The optional W3C context received with the request.</param>
    /// <returns>The started activity, or <see langword="null" /> when no listener samples it.</returns>
    /// <remarks>
    ///     Invalid propagated context is ignored and increments
    ///     <c>portia.transport.trace_context.invalid</c>. The invocation decides whether valid
    ///     context is a parent or a link.
    /// </remarks>
    public static Activity? StartProcess(string requestName, RequestInvocation invocation,
        RequestTraceContext? propagated)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ActivityContext parent = default;
        if (propagated is { } context && !context.TryParse(out parent))
        {
            InvalidTraceContext.Add(1,
                new KeyValuePair<string, object?>("portia.transport.name", invocation.TransportName));
        }

        if (!ActivitySource.HasListeners())
        {
            return null;
        }

        var linked = invocation.TraceRelationship == RequestTraceRelationship.Link;
        var links = linked && parent != default ? new[] { new ActivityLink(parent) } : null;
        var activity = ActivitySource.StartActivity(ProcessActivityName, ActivityKind.Consumer,
            linked ? default : parent, null, links);
        if (activity?.IsAllDataRequested == true)
        {
            _ = activity.SetTag("portia.request.name", requestName);
            _ = activity.SetTag("portia.transport.name", invocation.TransportName);
            if (invocation.MessagingSystem is not null)
            {
                _ = activity.SetTag("messaging.system", invocation.MessagingSystem);
                _ = activity.SetTag("messaging.operation.type", "process");
            }
        }

        return activity;
    }

    /// <summary>Captures the current activity's W3C trace-parent and trace-state fields.</summary>
    /// <returns>The propagation-safe context, or <see langword="null" /> when no activity is current.</returns>
    /// <remarks>Baggage, credentials, claims, and application payload data are never captured.</remarks>
    public static RequestTraceContext? CaptureTraceContext() =>
        Activity.Current is { Id: { } id } current ? new RequestTraceContext(id, current.TraceStateString) : null;

    /// <summary>Captures a monotonic timestamp suitable for the duration-recording methods.</summary>
    /// <returns>A timestamp produced by <see cref="Stopwatch.GetTimestamp()" />.</returns>
    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    static double Seconds(long started) => Stopwatch.GetElapsedTime(started).TotalSeconds;

    /// <summary>Increments the active-request instrument.</summary>
    /// <param name="requestName">The startup-bounded generated request discriminator.</param>
    /// <param name="transport">The stable transport identifier.</param>
    public static void RequestStarted(string requestName, string transport) => RequestActive.Add(1,
        new KeyValuePair<string, object?>("portia.request.name", requestName),
        new KeyValuePair<string, object?>("portia.transport.name", transport));

    /// <summary>Decrements the active-request instrument and records request duration.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="requestName">The startup-bounded generated request discriminator.</param>
    /// <param name="transport">The stable transport identifier.</param>
    /// <param name="outcome">A stable outcome returned by <see cref="Outcome" /> or another documented closed-set value.</param>
    public static void RequestFinished(long started, string requestName, string transport, string outcome)
    {
        RequestActive.Add(-1, new KeyValuePair<string, object?>("portia.request.name", requestName),
            new KeyValuePair<string, object?>("portia.transport.name", transport));
        RequestDuration.Record(Seconds(started),
            new KeyValuePair<string, object?>("portia.request.name", requestName),
            new KeyValuePair<string, object?>("portia.transport.name", transport),
            new KeyValuePair<string, object?>("portia.outcome", outcome));
    }

    /// <summary>Records exactly one final outcome for a transport delivery.</summary>
    /// <param name="requestName">The startup-bounded generated request discriminator.</param>
    /// <param name="transport">The stable transport shape.</param>
    /// <param name="outcome">The delivery's closed-set final outcome.</param>
    public static void RecordDelivery(string requestName, string transport, RequestDeliveryOutcome outcome) =>
        RequestDelivery.Add(1, new KeyValuePair<string, object?>("portia.request.name", requestName),
            new KeyValuePair<string, object?>("portia.transport.name", transport),
            new KeyValuePair<string, object?>("portia.outcome", DeliveryOutcomeName(outcome)));

    /// <summary>Records the duration and outcome of one authorization policy or stage.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="policy">The startup-bounded policy or authorizer name.</param>
    /// <param name="stage">The stable authorization stage.</param>
    /// <param name="outcome">The stable authorization outcome.</param>
    public static void AuthorizationFinished(long started, string policy, string stage, string outcome) =>
        AuthorizationDuration.Record(Seconds(started),
            new KeyValuePair<string, object?>("portia.component.name", policy),
            new KeyValuePair<string, object?>("portia.stage", stage),
            new KeyValuePair<string, object?>("portia.outcome", outcome));

    /// <summary>Records the duration and outcome of one transport operation.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="transport">The stable transport identifier.</param>
    /// <param name="operation">The stable transport operation.</param>
    /// <param name="outcome">The stable operation outcome.</param>
    public static void TransportFinished(long started, string transport, string operation, string outcome) =>
        TransportDuration.Record(Seconds(started),
            new KeyValuePair<string, object?>("portia.transport.name", transport),
            new KeyValuePair<string, object?>("portia.operation", operation),
            new KeyValuePair<string, object?>("portia.outcome", outcome));

    /// <summary>Records an aggregate operation and, when nonzero, its event count.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="operation">The stable aggregate operation.</param>
    /// <param name="outcome">The stable operation outcome.</param>
    /// <param name="eventCount">The number of events handled; zero suppresses the count instrument.</param>
    public static void AggregateFinished(long started, string operation, string outcome, int eventCount = 0)
    {
        AggregateDuration.Record(Seconds(started), new KeyValuePair<string, object?>("portia.operation", operation),
            new KeyValuePair<string, object?>("portia.outcome", outcome));
        if (eventCount > 0)
        {
            AggregateEvents.Add(eventCount, new KeyValuePair<string, object?>("portia.operation", operation));
        }
    }

    /// <summary>Records an event-store operation and, when nonzero, its event count.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="operation">The stable event-store operation.</param>
    /// <param name="scope">The stable stream or pattern scope.</param>
    /// <param name="outcome">The stable operation outcome.</param>
    /// <param name="eventCount">The number of events handled; zero suppresses the count instrument.</param>
    public static void EventStoreFinished(long started, string operation, string scope, string outcome,
        int eventCount = 0)
    {
        EventStoreDuration.Record(Seconds(started),
            new KeyValuePair<string, object?>("portia.operation", operation),
            new KeyValuePair<string, object?>("portia.scope", scope),
            new KeyValuePair<string, object?>("portia.outcome", outcome));
        if (eventCount > 0)
        {
            EventStoreEvents.Add(eventCount, new KeyValuePair<string, object?>("portia.operation", operation),
                new KeyValuePair<string, object?>("portia.scope", scope));
        }
    }

    /// <summary>Records a processor batch, its event count, and optional commit lag.</summary>
    /// <param name="started">The value returned by <see cref="StartTimestamp" />.</param>
    /// <param name="component">The startup-bounded registered processor name.</param>
    /// <param name="runner">The stable processor-runner kind.</param>
    /// <param name="outcome">The stable batch outcome.</param>
    /// <param name="eventCount">The number of events in the batch.</param>
    /// <param name="lastOccurrence">
    ///     The occurrence time of the last committed event, or
    ///     <see langword="null" /> to omit lag. Negative lag is clamped to zero.
    /// </param>
    public static void ProcessorBatchFinished(long started, string component, string runner, string outcome,
        int eventCount, DateTimeOffset? lastOccurrence = null)
    {
        ProcessorDuration.Record(Seconds(started),
            new KeyValuePair<string, object?>("portia.component.name", component),
            new KeyValuePair<string, object?>("portia.runner.name", runner),
            new KeyValuePair<string, object?>("portia.outcome", outcome));
        if (eventCount > 0)
        {
            ProcessorEvents.Add(eventCount, new KeyValuePair<string, object?>("portia.component.name", component),
                new KeyValuePair<string, object?>("portia.runner.name", runner));
        }

        if (lastOccurrence is { } occurrence)
        {
            ProcessorLag.Record(Math.Max(0, (DateTimeOffset.UtcNow - occurrence).TotalSeconds),
                new KeyValuePair<string, object?>("portia.component.name", component),
                new KeyValuePair<string, object?>("portia.runner.name", runner));
        }
    }

    /// <summary>Records a balanced workload lifecycle transition and optional information log.</summary>
    /// <param name="component">The startup-bounded registered workload name.</param>
    /// <param name="scope">The stable workload scope.</param>
    /// <param name="active"><see langword="true" /> when acquired; <see langword="false" /> when released.</param>
    /// <param name="logger">An optional logger for the lifecycle transition.</param>
    public static void RecordWorkload(string component, string scope, bool active, ILogger? logger = null)
    {
        WorkloadActive.Add(active ? 1 : -1,
            new KeyValuePair<string, object?>("portia.component.name", component),
            new KeyValuePair<string, object?>("portia.scope", scope));
        if (logger is not null)
        {
            LogWorkload(logger, component, scope, active);
        }
    }

    /// <summary>Increments the worker-restart counter.</summary>
    /// <param name="runner">The stable runner name.</param>
    /// <param name="stage">The stable restart stage.</param>
    public static void RecordWorkerRestart(string runner, string stage) => WorkerRestart.Add(1,
        new KeyValuePair<string, object?>("portia.runner.name", runner),
        new KeyValuePair<string, object?>("portia.stage", stage));

    /// <summary>Records a stable outcome and error status on a sampled request activity.</summary>
    /// <param name="activity">The request activity, or <see langword="null" /> when unsampled.</param>
    /// <param name="isSuccess">Whether request handling succeeded.</param>
    /// <param name="error">The expected request error when handling failed.</param>
    public static void RecordOutcome(Activity? activity, bool isSuccess, RequestError? error)
    {
        if (activity == null)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            _ = activity.SetTag("portia.outcome", Outcome(isSuccess, error));
        }

        _ = activity.SetStatus(isSuccess ? ActivityStatusCode.Unset : ActivityStatusCode.Error);
    }

    /// <summary>Completes a request activity for caller-requested cancellation.</summary>
    internal static void RecordCanceled(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            _ = activity.SetTag("portia.outcome", "canceled");
        }

        _ = activity.SetStatus(ActivityStatusCode.Unset);
    }

    /// <summary>Completes a request activity for an unexpected failure.</summary>
    internal static void RecordFault(Activity? activity, Exception? exception = null)
    {
        if (activity is null)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            _ = activity.SetTag("portia.outcome", "fault");
            if (exception is not null)
            {
                _ = activity.AddException(exception);
            }
        }

        _ = activity.SetStatus(ActivityStatusCode.Error);
    }

    /// <summary>Maps a request result to the stable outcome vocabulary.</summary>
    /// <param name="success">Whether the request succeeded.</param>
    /// <param name="error">The expected request error when unsuccessful.</param>
    /// <returns><c>success</c>, a known request-error kind, or <c>fault</c>.</returns>
    public static string Outcome(bool success, RequestError? error = null) => success
        ? "success"
        : error?.Kind switch
        {
            RequestErrorKind.Validation => "validation",
            RequestErrorKind.Unauthorized => "unauthorized",
            RequestErrorKind.Forbidden => "forbidden",
            RequestErrorKind.NotFound => "not_found",
            RequestErrorKind.Conflict => "conflict",
            _ => "fault"
        };

    /// <summary>Records a bounded background-fault metric and structured error log.</summary>
    /// <param name="runnerName">The stable runner name.</param>
    /// <param name="stage">The phase of the runner's work that faulted.</param>
    /// <param name="exception">The unexpected exception, when available.</param>
    /// <param name="logger">An optional logger for the structured fault event.</param>
    /// <remarks>
    ///     This method never starts an activity. When an activity is already current,
    ///     <paramref name="exception" /> is attached to it as an exception event. Metrics retain only
    ///     the bounded runner and stage. The structured log retains the exception type and full
    ///     exception for operator diagnosis.
    /// </remarks>
    public static void RecordRunnerFault(string runnerName, RunnerFaultStage stage, Exception? exception = null,
        ILogger? logger = null)
    {
        WorkerFailure.Add(1, new KeyValuePair<string, object?>("portia.runner.name", runnerName),
            new KeyValuePair<string, object?>("portia.stage", StageName(stage)));
        if (logger is not null)
        {
            LogRunnerFault(logger, runnerName, StageName(stage), exception?.GetType().Name ?? "unexpected_termination",
                exception);
        }

        if (exception is not null && Activity.Current is { IsAllDataRequested: true } activity)
        {
            _ = activity.AddException(exception);
        }
    }

    /// <summary>Maps an authorization stage to its stable lowercase metric vocabulary.</summary>
    internal static string StageName(AuthorizationStage stage) => stage switch
    {
        AuthorizationStage.Principal => "principal",
        AuthorizationStage.ResourceAccess => "resourceaccess",
        AuthorizationStage.StepUp => "stepup",
        _ => stage.ToString().ToLowerInvariant()
    };

    /// <summary>Maps a fault stage to its stable lowercase log vocabulary.</summary>
    internal static string StageName(RunnerFaultStage stage) => stage switch
    {
        RunnerFaultStage.Acquisition => "acquisition",
        RunnerFaultStage.Renewal => "renewal",
        RunnerFaultStage.Watch => "watch",
        RunnerFaultStage.Validation => "validation",
        RunnerFaultStage.Notification => "notification",
        RunnerFaultStage.Workload => "workload",
        RunnerFaultStage.Cleanup => "cleanup",
        RunnerFaultStage.Execution or _ => "execution"
    };

    /// <summary>Records a fleet-assignment gauge transition and optional information log.</summary>
    /// <param name="workerId">The worker identity associated with the transition.</param>
    /// <param name="partition">The partition identity associated with the transition.</param>
    /// <param name="assigned"><see langword="true" /> when assigned; <see langword="false" /> when released.</param>
    /// <param name="logger">An optional logger for the lifecycle transition.</param>
    /// <remarks>Worker and partition identities are deliberately excluded from both metrics and logs.</remarks>
    public static void RecordFleetAssignment(string workerId, string partition, bool assigned, ILogger? logger = null)
    {
        FleetAssignmentActive.Add(assigned ? 1 : -1,
            new KeyValuePair<string, object?>("portia.scope", "partition"));
        if (logger is not null)
        {
            LogFleetAssignment(logger, assigned);
        }
    }

    /// <summary>Logs one irrecoverably lost one-way delivery without business data.</summary>
    internal static void RecordLostDelivery(string requestName, string transport, ILogger? logger)
    {
        if (logger is not null)
        {
            LogLostDelivery(logger, requestName, transport);
        }
    }

    /// <summary>Logs one queue delivery successfully handled by terminal policy.</summary>
    internal static void RecordTerminalDelivery(string requestName, string transport, ILogger? logger)
    {
        if (logger is not null)
        {
            LogTerminalDelivery(logger, requestName, transport);
        }
    }

    /// <summary>Logs use of the single-process coordinator fallback.</summary>
    internal static void RecordSingleProcessCoordinator(ILogger? logger)
    {
        if (logger is not null)
        {
            LogSingleProcessCoordinator(logger);
        }
    }

    static string DeliveryOutcomeName(RequestDeliveryOutcome outcome) => outcome switch
    {
        RequestDeliveryOutcome.Completed => "completed",
        RequestDeliveryOutcome.Abandoned => "abandoned",
        RequestDeliveryOutcome.Terminal => "terminal",
        RequestDeliveryOutcome.Lost => "lost",
        RequestDeliveryOutcome.Canceled => "canceled",
        RequestDeliveryOutcome.Fault => "fault",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "Portia fleet partition assignment active: {Active}")]
    static partial void LogFleetAssignment(ILogger logger, bool active);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Portia {RunnerName} fault at {Stage} ({ErrorType})")]
    static partial void LogRunnerFault(ILogger logger, string runnerName, string stage, string errorType,
        Exception? exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug,
        Message = "Portia workload {Component} ({Scope}) active: {Active}")]
    static partial void LogWorkload(ILogger logger, string component, string scope, bool active);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning,
        Message = "Portia lost one-way request {RequestName} on {Transport}")]
    static partial void LogLostDelivery(ILogger logger, string requestName, string transport);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "Portia terminalized queue request {RequestName} on {Transport}")]
    static partial void LogTerminalDelivery(ILogger logger, string requestName, string transport);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Warning,
        Message =
            "No IWorkloadCoordinator is registered; owning every workload in this process. That is correct for a single worker replica only — register a distributed coordinator before scaling workers out.")]
    static partial void LogSingleProcessCoordinator(ILogger logger);
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// The single <see cref="ActivitySource" /> every request dispatch is traced through, regardless
/// of which transport it arrived on — instrumented once at <see cref="IRequestBus" /> dispatch
/// (the one chokepoint every transport already funnels through), not per-transport. Uses the
/// .NET base class library's own <see cref="Activity" /> API, which is OpenTelemetry-compatible
/// by construction — an app wires up the actual OpenTelemetry SDK and exporters itself; Portia
/// only needs to produce activities, never take a dependency on OpenTelemetry packages directly.
/// </summary>
public static partial class PortiaTelemetry
{
    /// <summary>
    /// Gets the name every Portia activity and instrument is emitted under, for an app's
    /// OpenTelemetry configuration to subscribe to (e.g. <c>AddSource("Cntryl.Portia")</c>).
    /// </summary>
    public const string SourceName = "Cntryl.Portia";

    /// <summary>
    /// Gets the shared activity source every request dispatch starts an activity on.
    /// </summary>
    public static ActivitySource ActivitySource { get; } = new(SourceName);

    /// <summary>
    /// Records a dispatch's outcome onto its activity — tagging success, and on failure, setting
    /// the activity's error status with the failure's category and message. Called by generated
    /// dispatch code, not normally by application code directly.
    /// </summary>
    /// <param name="activity">The dispatch's activity, or <see langword="null" /> if nothing is
    /// currently listening to <see cref="ActivitySource" />.</param>
    /// <param name="isSuccess">Whether the dispatch succeeded.</param>
    /// <param name="error">The failure, when <paramref name="isSuccess" /> is <see langword="false" />.</param>
    public static void RecordOutcome(Activity? activity, bool isSuccess, RequestError? error)
    {
        if (activity is null)
            return;

        _ = activity.SetTag("portia.success", isSuccess);

        if (isSuccess)
            return;

        _ = activity.SetTag("portia.error_kind", error?.Kind.ToString());
        _ = activity.SetStatus(ActivityStatusCode.Error, error?.Message);
    }

    /// <summary>
    /// Records a background runner (<c>QueueRunner</c>, <c>LiveRequestRunner</c>,
    /// <c>MultiTenantRunner</c>, <c>FleetPartitionRunner</c>, ...) silently dropping or
    /// abandoning work, so it's visible to whatever's observing <see cref="ActivitySource" />
    /// instead of vanishing — nothing in Portia should ever require debugging the framework
    /// itself to explain a stalled or disappearing request.
    ///
    /// <c>ActivitySource.StartActivity(string)</c> returns <see langword="null" /> —
    /// silently, with no other signal — unless something has already registered an
    /// <see cref="ActivityListener" /> for <see cref="SourceName" />: the
    /// default state for an app that hasn't configured OpenTelemetry yet, which is exactly the
    /// app most likely to need this visibility. <paramref name="logger" /> is the fallback for
    /// that case — logged unconditionally at <see cref="LogLevel.Error" /> whenever it's
    /// supplied, independent of whether anything is listening to the activity source.
    /// </summary>
    /// <param name="runnerName">The runner reporting the fault (typically <c>nameof(...)</c>).</param>
    /// <param name="reason">A short, stable description of what went wrong.</param>
    /// <param name="exception">The exception that caused the fault, if any.</param>
    /// <param name="logger">
    /// A logger to also report the fault through, independent of OpenTelemetry configuration.
    /// Every runner accepts one as an optional constructor parameter for exactly this purpose.
    /// </param>
    public static void RecordRunnerFault(string runnerName, string reason, Exception? exception = null, ILogger? logger = null)
    {
        if (logger is not null)
            LogRunnerFault(logger, runnerName, reason, exception);

        using var activity = ActivitySource.StartActivity($"Portia {runnerName} fault");

        if (activity is null)
            return;

        _ = activity.SetTag("portia.runner", runnerName);
        _ = activity.SetTag("portia.fault_reason", reason);

        if (exception is not null)
            _ = activity.AddException(exception);

        _ = activity.SetStatus(ActivityStatusCode.Error, exception?.Message ?? reason);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Portia {RunnerName} fault: {Reason}")]
    static partial void LogRunnerFault(ILogger logger, string runnerName, string reason, Exception? exception);
}

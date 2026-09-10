using System.Diagnostics;

namespace Cntryl.Portia;

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

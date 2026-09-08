using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Verifies that <see cref="PortiaTelemetry.RecordRunnerFault" /> is still visible with no
/// <see cref="System.Diagnostics.ActivityListener" /> attached at all — the default state for an
/// app that hasn't configured OpenTelemetry yet. Without this, a fault reported only via
/// <see cref="System.Diagnostics.ActivitySource" /> is a complete no-op in that state
/// (<c>ActivitySource.StartActivity</c> returns <see langword="null" /> with nothing listening),
/// silently defeating the entire point of reporting it at all.
/// </summary>
public sealed class PortiaTelemetryLoggerFallbackTests
{
    /// <summary>
    /// Verifies that a fault is logged even when nothing is listening to
    /// <see cref="PortiaTelemetry.ActivitySource" />, as long as an <see cref="ILogger" /> was
    /// supplied.
    /// </summary>
    [Fact]
    public void ShouldLogFaultWhenNoActivityListenerIsAttached()
    {
        var logger = new CapturingLogger();
        var exception = new InvalidOperationException("boom");

        PortiaTelemetry.RecordRunnerFault("TestRunner", "test reason", exception, logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Contains("TestRunner", entry.Message, StringComparison.Ordinal);
        Assert.Contains("execution", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test reason", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that omitting the logger (the previous call shape) still works — the fallback is
    /// additive, not a required parameter every existing call site has to be rewritten for.
    /// </summary>
    [Fact]
    public void ShouldNotThrowWhenLoggerIsOmitted() =>
        PortiaTelemetry.RecordRunnerFault("TestRunner", "test reason", new InvalidOperationException("boom"));

    sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}

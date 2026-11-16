namespace Cntryl.Portia;

// The one retry-delay rule for a failing workload: the poll interval doubled per consecutive failure, up to
// the configured ceiling. Hosting waits on it and the operational journal gates the next attempt on it, so
// both read it from here.
static class WorkloadFailureBackoff
{
    internal static TimeSpan Delay(TimeSpan pollInterval, TimeSpan maximumFailureDelay, int consecutiveFailures)
    {
        var maximumTicks = maximumFailureDelay.Ticks;
        var initialTicks = Math.Min(pollInterval.Ticks, maximumTicks);
        var exponent = Math.Min(consecutiveFailures - 1, 30);
        var ticks = initialTicks > maximumTicks >> exponent
            ? maximumTicks
            : initialTicks << exponent;
        return TimeSpan.FromTicks(Math.Min(ticks, maximumTicks));
    }
}

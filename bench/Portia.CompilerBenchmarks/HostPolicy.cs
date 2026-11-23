namespace Cntryl.Portia.CompilerBenchmarks;

sealed record HostInterval(double Seconds, double BusyFraction, int Processors, double WorkerCpuMilliseconds,
    double ControllerCpuMilliseconds, long SwapGrowthBytes, int MemoryPressure, bool Supported, int? ThermalState = 0, bool LowPowerMode = false, long ThrottledMicroseconds = 0);
sealed record HostVerdict(bool Accepted, double BackgroundCpuFraction, string[] Reasons);

static class HostPolicy
{
    public const double BackgroundCpuLimit = .10;

    public static HostVerdict Evaluate(HostInterval interval)
    {
        var reasons = new List<string>();

        if (!interval.Supported)
            reasons.Add("host telemetry unavailable");


        if (!double.IsFinite(interval.Seconds) || interval.Seconds <= 0 || interval.Processors <= 0
            || !double.IsFinite(interval.BusyFraction) || interval.BusyFraction is < 0 or > 1
            || !double.IsFinite(interval.WorkerCpuMilliseconds) || !double.IsFinite(interval.ControllerCpuMilliseconds)
            || interval.WorkerCpuMilliseconds < 0 || interval.ControllerCpuMilliseconds < 0)
            reasons.Add("invalid host counters");

        var capacity = interval.Seconds * interval.Processors * 1000;
        var background = capacity > 0
            ? Math.Max(0, interval.BusyFraction - (interval.WorkerCpuMilliseconds + interval.ControllerCpuMilliseconds) / capacity)
            : double.NaN;

        if (!double.IsFinite(background) || background > BackgroundCpuLimit)
            reasons.Add("background CPU exceeds 10 percent");

        if (interval.MemoryPressure != 1)
            reasons.Add("memory pressure");

        if (interval.ThermalState is null)
            reasons.Add("thermal telemetry unavailable");
        else if (interval.ThermalState != 0)
            reasons.Add("thermal pressure");

        if (interval.LowPowerMode)
            reasons.Add("low power mode");

        if (interval.ThrottledMicroseconds > 0)
            reasons.Add("CPU throttled");


        if (interval.SwapGrowthBytes > 0)
            reasons.Add("swap usage increased");

        return new HostVerdict(reasons.Count == 0, background, reasons.ToArray());
    }
}

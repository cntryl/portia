using System.Diagnostics;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed record HostObservation(HostCounters Before, HostCounters After, HostInterval Interval, HostVerdict Verdict);
sealed record HostRun(string Policy, bool Accepted, HostObservation[] Observations, string[] Reasons);

sealed class HostMonitor : IDisposable
{
    internal static bool LinuxArm64Vm => OperatingSystem.IsLinux()
        && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
        && Environment.GetEnvironmentVariable("PORTIA_COMPILER_HOST_POLICY") == "linux-arm64-vm";

    public static string Policy => LinuxArm64Vm ? "linux-arm64-vm-1" : "macos-reference-host-1";
    readonly Process _controller = Process.GetCurrentProcess();
    readonly Process? _worker;
    readonly List<HostObservation> _observations = [];
    HostCounters _before = HostCounters.Capture();
    double _controllerCpu;
    double _workerCpu;

    public HostMonitor(Process? worker = null)
    {
        _worker = worker;
        _controllerCpu = _controller.TotalProcessorTime.TotalMilliseconds;
    }

    static double? Cpu(Process process)
    {
        try
        { return process.TotalProcessorTime.TotalMilliseconds; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return null; }
    }

    public bool Observe()
    {
        var cpu = _worker is null ? 0 : Cpu(_worker);
        // The process may have exited between the exit check and counter read. Its report supplies the final CPU count.
        return cpu is null ? true : Observe(cpu.Value);
    }

    bool Observe(double workerCpu)
    {
        var after = HostCounters.Capture();
        var controller = _controller.TotalProcessorTime.TotalMilliseconds;
        var interval = HostCounters.Interval(_before, after, workerCpu - _workerCpu, controller - _controllerCpu);
        var verdict = HostPolicy.Evaluate(interval, LinuxArm64Vm);
        _observations.Add(new HostObservation(_before, after, interval, verdict));
        _before = after;
        _controllerCpu = controller;
        _workerCpu = workerCpu;
        return verdict.Accepted;
    }

    public HostRun Complete(double? reportedCpu = null)
    {
        var observed = _worker is null ? 0 : Cpu(_worker);
        var finalCpu = _worker is null ? 0 : Math.Max(_workerCpu, Math.Max(observed ?? 0, reportedCpu ?? 0));
        MergeShortFinalInterval(finalCpu);
        var reasons = _observations.SelectMany(row => row.Verdict.Reasons).Distinct(StringComparer.Ordinal).ToArray();
        return new HostRun(Policy, reasons.Length == 0, _observations.ToArray(), reasons);
    }

    void MergeShortFinalInterval(double workerCpu)
    {
        // Linux CPU accounting is quantized. Fold a sub-sample exit tail into the preceding
        // accepted interval so the same 10% budget is evaluated over a measurable window.
        if (LinuxArm64Vm && Stopwatch.GetElapsedTime(_before.Timestamp) < TimeSpan.FromMilliseconds(500)
            && _observations.LastOrDefault() is { Verdict.Accepted: true } previous)
        {
            _observations.RemoveAt(_observations.Count - 1);
            _before = previous.Before;
            _workerCpu -= previous.Interval.WorkerCpuMilliseconds;
            _controllerCpu -= previous.Interval.ControllerCpuMilliseconds;
        }

        _ = Observe(workerCpu);
    }

    public static async Task<HostRun> IdleAsync(int seconds, CancellationToken ct)
    {
        using var monitor = new HostMonitor();

        for (var i = 0; i < seconds; i++)
        {
            try
            { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            { return new HostRun(Policy, false, monitor._observations.ToArray(), ["host preflight canceled"]); }

            if (!monitor.Observe())
                break;
        }
        // Observe() already closed every full interval; avoid an unmeasurably short trailing interval.

        var reasons = monitor._observations.SelectMany(row => row.Verdict.Reasons).Distinct(StringComparer.Ordinal).ToArray();
        return new HostRun(Policy, monitor._observations.Count == seconds && reasons.Length == 0,
            monitor._observations.ToArray(), reasons);
    }

    public void Dispose() => _controller.Dispose();
}

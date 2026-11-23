using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed record HostCounters(long Timestamp, DateTimeOffset Utc, ulong TotalCpu, ulong IdleCpu, int Processors,
    long SwapBytes, int MemoryPressure, int? ThermalState, bool LowPowerMode, long ThrottledMicroseconds,
    bool Supported, string? Error)
{
    public static HostCounters Capture()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return Mac();


            if (OperatingSystem.IsLinux())
                return Linux();

            return Missing("Host counters support macOS and Linux.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException or FormatException or OverflowException)
        {
            return Missing(error.Message);
        }
    }

    static HostCounters Missing(string error) => new(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, 0, 0, 0,
        0, 0, null, false, 0, false, error);

    static HostCounters Mac()
    {
        var cpu = new NativeMac.CpuLoad();
        uint count = 4;

        if (NativeMac.HostStatistics(NativeMac.Host.Value, 3, ref cpu, ref count) != 0 || count != 4)
            throw new InvalidOperationException("Could not read host CPU counters.");

        var swap = new NativeMac.SwapUsage();
        var length = (nuint)Marshal.SizeOf<NativeMac.SwapUsage>();

        if (NativeMac.ReadSwap("vm.swapusage", ref swap, ref length, 0, 0) != 0)
            throw new InvalidOperationException("Could not read swap usage.");

        var info = NativeMac.ProcessInfo.Value;
        return new HostCounters(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow,
            (ulong)cpu.User + cpu.System + cpu.Idle + cpu.Nice, cpu.Idle,
            NativeMac.ReadInteger("hw.logicalcpu"), checked((long)swap.Used),
            NativeMac.ReadInteger("kern.memorystatus_vm_pressure_level"),
            checked((int)NativeMac.Send(info, NativeMac.Selector("thermalState"))),
            NativeMac.SendBoolean(info, NativeMac.Selector("isLowPowerModeEnabled")), 0, true, null);
    }

    static HostCounters Linux()
    {
        var lines = File.ReadAllLines("/proc/stat");
        var cpu = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var memory = File.ReadAllLines("/proc/meminfo").Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0].TrimEnd(':'), parts => long.Parse(parts[1], CultureInfo.InvariantCulture) * 1024,
                StringComparer.Ordinal);
        var throttled = 0L;

        if (File.Exists("/sys/fs/cgroup/cpu.stat"))
        {
            var row = File.ReadLines("/sys/fs/cgroup/cpu.stat").FirstOrDefault(line => line.StartsWith("throttled_usec ", StringComparison.Ordinal));

            if (row is not null)
                throttled = long.Parse(row.AsSpan("throttled_usec ".Length), CultureInfo.InvariantCulture);
        }
        // Linux temperature/throttle sources vary by host. Unknown thermal state cannot qualify a timing run.

        return new HostCounters(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, cpu.Aggregate(0UL, (sum, value) => sum + value),
            cpu[3] + cpu[4], lines.Count(line => line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal) && char.IsDigit(line[3])),
            memory["SwapTotal"] - memory["SwapFree"], memory["MemAvailable"] >= memory["MemTotal"] / 10 ? 1 : 2,
            null, false, throttled, true, null);
    }

    public static HostInterval Interval(
        HostCounters before,
        HostCounters after,
        double workerCpuMilliseconds,
        double controllerCpuMilliseconds
    )
    {
        var valid = before.Supported && after.Supported && before.Processors == after.Processors
            && after.TotalCpu > before.TotalCpu && after.IdleCpu >= before.IdleCpu;
        var busy = valid ? 1 - (double)(after.IdleCpu - before.IdleCpu) / (after.TotalCpu - before.TotalCpu) : double.NaN;
        return new HostInterval(Stopwatch.GetElapsedTime(before.Timestamp, after.Timestamp).TotalSeconds, busy,
            after.Processors, workerCpuMilliseconds, controllerCpuMilliseconds, after.SwapBytes - before.SwapBytes,
            Math.Max(before.MemoryPressure, after.MemoryPressure), valid,
            before.ThermalState is null || after.ThermalState is null ? null : Math.Max(before.ThermalState.Value, after.ThermalState.Value),
            before.LowPowerMode || after.LowPowerMode, after.ThrottledMicroseconds - before.ThrottledMicroseconds);
    }
}

static partial class NativeMac
{
    const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    internal static readonly Lazy<uint> Host = new(MachHostSelf);
    internal static readonly Lazy<nint> ProcessInfo = new(() =>
    {
        _ = NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        var type = GetClass("NSProcessInfo");
        var result = Send(type, Selector("processInfo"));
        return result != 0 ? result : throw new InvalidOperationException("Could not read NSProcessInfo.");
    });

    [StructLayout(LayoutKind.Sequential)]
    internal struct CpuLoad { public uint User; public uint System; public uint Idle; public uint Nice; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SwapUsage { public ulong Total; public ulong Available; public ulong Used; public uint PageSize; public int Encrypted; }

    [LibraryImport(SystemLibrary, EntryPoint = "mach_host_self")]
    private static partial uint MachHostSelf();
    [LibraryImport(SystemLibrary, EntryPoint = "host_statistics")]
    internal static partial int HostStatistics(uint host, int flavor, ref CpuLoad cpu, ref uint count);
    [LibraryImport(SystemLibrary, EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ReadSwap(string name, ref SwapUsage value, ref nuint length, nint newValue, nuint newLength);
    [LibraryImport(SystemLibrary, EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ReadInt(string name, ref int value, ref nuint length, nint newValue, nuint newLength);
    [LibraryImport(ObjectiveCLibrary, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);
    [LibraryImport(ObjectiveCLibrary, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Selector(string name);
    [LibraryImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector);
    [LibraryImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SendBoolean(nint receiver, nint selector);

    internal static int ReadInteger(string name)
    {
        var value = 0;
        nuint length = sizeof(int);
        return ReadInt(name, ref value, ref length, 0, 0) == 0 ? value
            : throw new InvalidOperationException("Could not read " + name);
    }
}

using BenchmarkDotNet.Running;
using Cntryl.Portia;

if (args.Length == 2 && args[0] == "--prometheus-qualification")
    await PrometheusQualification.RunAsync(args[1]);
else if (args.Length >= 3 && args[0] == "--fitz-append-campaign")
    await FitzAppendCampaign.RunAsync(args[1], new Uri(args[2]), args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 30);
else if (args.Length == 3 && args[0] == "--fitz-restart-readback")
    await FitzAppendCampaign.ReadbackAsync(args[1], new Uri(args[2]));
else if (args.Length == 1 && args[0] == "--runtime-qualification")
{
    AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
    await RuntimeQualification.RunAsync();
}
else if (args.Contains("--processor-pass-allocation-probe", StringComparer.Ordinal))
    ProcessorPassAllocationProbe.Run();
else
    BenchmarkSwitcher.FromAssembly(typeof(UuidV5Benchmarks).Assembly).Run(args);

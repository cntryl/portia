using BenchmarkDotNet.Running;
using Cntryl.Portia;

if (args.Length == 2 && args[0] == "--prometheus-qualification")
    await PrometheusQualification.RunAsync(args[1]);
else if (args.Contains("--processor-pass-allocation-probe", StringComparer.Ordinal))
    ProcessorPassAllocationProbe.Run();
else
    BenchmarkSwitcher.FromAssembly(typeof(UuidV5Benchmarks).Assembly).Run(args);

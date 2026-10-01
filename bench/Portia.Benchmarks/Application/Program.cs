using BenchmarkDotNet.Running;
using Cntryl.Portia;

if (args.Contains("--processor-pass-allocation-probe", StringComparer.Ordinal))
    ProcessorPassAllocationProbe.Run();
else
    BenchmarkSwitcher.FromAssembly(typeof(UuidV5Benchmarks).Assembly).Run(args);

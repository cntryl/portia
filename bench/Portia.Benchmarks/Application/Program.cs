using BenchmarkDotNet.Running;
using Cntryl.Portia;

if (Environment.GetEnvironmentVariable("PORTIA_OPERATIONAL_WORKER") is "run" or "smoke")
    await OperationalWorkerBenchmark.RunAsync();
else if (Environment.GetEnvironmentVariable("PORTIA_OPERATIONAL_BASELINE_DIRECT") == "1")
    await OperationalJournalBaselineBenchmarks.RunDirectAsync();
else
    BenchmarkSwitcher.FromAssembly(typeof(UuidV5Benchmarks).Assembly).Run(args);

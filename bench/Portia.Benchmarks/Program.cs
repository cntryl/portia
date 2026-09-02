using BenchmarkDotNet.Running;
using Cntryl.Portia;

BenchmarkSwitcher.FromAssembly(typeof(UuidV5Benchmarks).Assembly).Run(args);

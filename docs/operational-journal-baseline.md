# Operational journal baseline

This is a measurement harness for issue #309. It exercises the real `FitzOperationalStore` against
an isolated Fitz route, retaining the provider's synchronous transaction durability. It measures
single-document reads, fenced retry transitions, status loads, and concurrent recovery transitions
for independent workloads. `History` is the number of retained attempt receipts (4 or 128) and
`Workloads` is the independent workload count (1 or 8).

Run from the repository root with Fitz available:

```sh
FITZ_TEST_ENDPOINT=ws://127.0.0.1:40929/ws \
  dotnet run --configuration Release --project bench/Portia.Benchmarks -- \
  --filter '*OperationalJournalBaselineBenchmarks*' \
  --exporters json markdown
```

The JSON artifact is written under `BenchmarkDotNet.Artifacts/results/`. BenchmarkDotNet reports
mean and percentile statistics when the percentile columns are enabled by the selected exporter;
retain that file with the run's exact commit, SDK, endpoint, hardware, broker version, durability
configuration, and command. The harness deliberately reports no synthesized throughput or byte
count: serialized state bytes can be obtained from the captured journal document, while operation
counts are one read or one fenced transition per benchmark invocation and `Workloads` transitions
for recovery. Do not compare runs unless these inputs and the benchmark job are identical.

This is a baseline, not an optimization claim. It does not measure application handler CPU,
network loss, broker failover, or a full worker process restart. A future comparison must preserve
the same route isolation, provider durability, history/workload matrix, and exact repair-source
and checkpoint assertions before declaring a regression margin.

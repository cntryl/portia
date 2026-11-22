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

When BenchmarkDotNet cannot recognize the installed SDK runtime, the same matrix can be run
through the direct harness. It emits 20 raw timed samples per row and p50/p95/p99 in milliseconds:

```sh
PORTIA_OPERATIONAL_BASELINE_DIRECT=1 \
FITZ_TEST_ENDPOINT=ws://127.0.0.1:40929/ws \
  dotnet run --configuration Release --project bench/Portia.Benchmarks --no-restore --no-build
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

## Direct run recorded 2026-11-22

The direct mode was run against `ws://127.0.0.1:40929/ws` on the local Apple Silicon host, using
the checked-out .NET 11 SDK and the broker's configured synchronous durability. The following is
the captured stdout summary; each row has 20 samples and the direct runner computes nearest-rank
percentiles. Values are milliseconds per benchmark invocation.

| History | Workloads | Operation | p50 | p95 | p99 |
|---:|---:|---|---:|---:|---:|
| 4 | 1 | read | 0.958 | 1.224 | 1.226 |
| 4 | 1 | retry | 4.348 | 4.712 | 7.054 |
| 4 | 1 | status | 1.401 | 1.519 | 1.528 |
| 4 | 1 | recovery | 4.232 | 4.505 | 4.858 |
| 4 | 8 | read | 0.735 | 0.764 | 0.768 |
| 4 | 8 | retry | 4.243 | 5.154 | 5.999 |
| 4 | 8 | status | 6.806 | 7.565 | 8.112 |
| 4 | 8 | recovery | 24.037 | 26.197 | 28.547 |
| 128 | 1 | read | 2.323 | 2.901 | 4.405 |
| 128 | 1 | retry | 8.079 | 8.991 | 9.142 |
| 128 | 1 | status | 2.668 | 3.052 | 3.859 |
| 128 | 1 | recovery | 7.044 | 11.066 | 12.449 |
| 128 | 8 | read | 1.791 | 2.048 | 3.382 |
| 128 | 8 | retry | 7.160 | 8.346 | 8.955 |
| 128 | 8 | status | 14.555 | 16.175 | 16.290 |
| 128 | 8 | recovery | 30.059 | 33.738 | 34.954 |

These are directional local observations, not a cross-revision regression claim. The run's raw
stdout is the durable evidence for this checkout; repeat the direct command after any change and
retain its complete output with the exact commit and broker configuration. The matrix suggests
that retained history increases transition cost and that concurrent recovery/status load scales
with workload count, but it does not isolate broker-side contention from client scheduling.

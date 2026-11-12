# M5 MCP lookup-position supplement

This extends the pre-optimization M5 workload with fixed-resource, final-template, and final-prompt lookups. It is a single descriptive capture, not a paired optimization claim.

- Captured: `2026-11-12T11:18:56.085026+00:00`
- Benchmark source commit: `ffcb5cdbfe07c24014f03fbc45df4793149185ae` (clean worktree)
- Benchmark-only patch commit for reproduction: `950544b805c3e8b91d61f4a4245b88a11d396a9b`
- M5 baseline commit: `3525d305b84d90da3bd8cb36ac688606f01d8088`; MCP source tree: `dcfa4715f2aede74df0a604d98c8f1138d643c4b`
- Benchmark project tree: `945e303b022257451f1eeeb98d6d2b38601b602d`
- Artifact SHA-256: `f64f2e3c41dfcb55cb1dcfaf141640b31f1185c3a6e7162466fe30ce78b7d090`
- SDK/runtime: .NET SDK `11.0.100`, `.NET 11.0.0` (11.0.0)
- Environment: macOS 27.0.1, Arm64, 10 logical processors, server GC `False`
- MCP packages: `ModelContextProtocol` 2.4.0, `ModelContextProtocol.AspNetCore` 2.4.0
- Workload: Streamable HTTP over loopback Kestrel and official SDK stdio child; catalogs 1/100/1,000; 4-KiB JSON payloads; telemetry disabled.
- Each configuration has 10 warmups, 30 sequential latency samples, and a separate 50-call throughput and allocation batch. Startup and MCP initialization occur outside latency samples.

## M5 baseline measurements

P50/P95 are microseconds. HTTP allocations include client and in-process server; stdio allocations cover the parent client process. All 30 raw rows and individual samples are in [raw.jsonl](raw.jsonl).

| Catalog | Transport | Fixed resource P50/P95 | First template P50/P95 | Final template P50/P95 | First prompt P50/P95 | Final prompt P50/P95 |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | http | 287.0 / 355.6 | 279.4 / 321.8 | 268.1 / 306.0 | 224.2 / 247.0 | 215.0 / 249.5 |
| 1 | stdio | 167.1 / 186.5 | 130.5 / 163.0 | 106.6 / 115.6 | 294.6 / 415.5 | 282.0 / 479.5 |
| 100 | http | 342.7 / 405.2 | 292.6 / 322.8 | 340.4 / 376.7 | 253.4 / 269.7 | 253.4 / 275.2 |
| 100 | stdio | 196.5 / 251.5 | 155.2 / 211.7 | 199.9 / 236.7 | 121.8 / 169.9 | 106.0 / 127.7 |
| 1,000 | http | 1,079.9 / 1,484.8 | 535.7 / 565.6 | 1,058.9 / 1,426.5 | 454.3 / 495.6 | 459.2 / 484.3 |
| 1,000 | stdio | 835.2 / 3,075.0 | 223.9 / 276.8 | 856.1 / 1,042.4 | 128.5 / 160.3 | 122.0 / 156.7 |

At catalog size 1, first and final positions refer to the same entry. At size 1,000, the final-template P50 is higher than the first-template P50 for both transports (http 1,058.9 vs 535.7 µs; stdio 856.1 vs 223.9 µs). The fixed resource, registered after all templates, also measured higher than the first-template control (http 1,079.9 vs 535.7 µs; stdio 835.2 vs 223.9 µs). First/final prompt client measurements are similar in this single capture; they do not independently establish an end-to-end prompt slowdown.

## Link to the paired M6 comparison

The optimization decision and paired direct-handler results are in [m6-decision.md](../mcp-protocol-2026-11-11/m6-decision.md), with the 10-pair raw data in [m6-lookup-paired.jsonl](../mcp-protocol-2026-11-11/m6-lookup-paired.jsonl). That comparison uses 500 latency samples after 100 warmups and a 1,000-call allocation/throughput batch; its paired 95% intervals for the final resource-template, fixed-resource, and final-prompt lookups at catalog sizes 100 and 1,000 are below zero. The M5 supplement establishes that the expensive lookup positions are present in the baseline workload; the paired M6 artifact establishes the measured candidate improvement. The two captures have distinct stated timing scopes and are not pooled.

## Reproduction

Apply benchmark-only commit `950544b805c3e8b91d61f4a4245b88a11d396a9b` to the documented baseline commit `3525d305b84d90da3bd8cb36ac688606f01d8088`, then run:

```sh
dotnet restore bench/Portia.Benchmarks/Portia.Benchmarks.csproj
dotnet build --configuration Release --no-restore bench/Portia.Benchmarks/Portia.Benchmarks.csproj
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --mcp-protocol-qualification lookup-position-supplement <output.jsonl>
python3 bench/Portia.Benchmarks/Mcp/summarize_mcp_lookup_supplement.py <output.jsonl>
```

The raw output records the exact source and benchmark trees, dependency input hashes, resolved package versions, workload, runtime, operating system, processor architecture, and every sample. The supplement omits other M5 telemetry modes, payload sizes, text/binary resources, tool calls, startup, discovery, and direct/REST controls; it does not replace the full M5 campaign.

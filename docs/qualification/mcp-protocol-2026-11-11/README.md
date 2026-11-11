# MCP protocol performance baseline

- Captured: 2026-11-11T17:01:32.347975+00:00
- Source: `212cdc8e538c1c487e4ed5b6198d314d7e171a4b` (worktree status: `clean`)
- SDK/runtime: .NET SDK 11.0.100, .NET 11.0.0 (11.0.0)
- Environment: macOS 27.0.1, Arm64, 10 logical processors, server GC `False`
- MCP packages: `ModelContextProtocol` 2.4.0, `ModelContextProtocol.AspNetCore` 2.4.0
- Warm operations: 30 sequential latency samples after 10 warmups; throughput batches contain 50 operations. Startup and initialize are single observations per configuration.
- The raw JSONL records every latency sample, throughput duration, process allocation delta, telemetry count, workload setting, and resolved package version.

## Startup and protocol initialization

HTTP startup and initialize are separate. The SDK stdio client combines child-process startup and initialize, so it has one row. Startup/initialize numbers are single observations; the first application startup includes cold JIT effects, so use them only as lifecycle diagnostics.

| Catalog | Telemetry | HTTP startup (ms) | HTTP initialize (ms) | stdio startup + initialize (ms) |
| ---: | --- | ---: | ---: | ---: |
| 1 | disabled | 69.8 | 73.3 | 140.3 |
| 1 | propagation | 3.1 | 1.1 | 147.1 |
| 1 | sampled-tracing | 1.9 | 0.8 | 138.0 |
| 1 | metrics | 2.4 | 0.5 | 139.0 |
| 1 | logging | 10.6 | 1.6 | 209.5 |
| 100 | disabled | 55.0 | 1.0 | 188.3 |
| 100 | propagation | 10.5 | 0.6 | 198.8 |
| 100 | sampled-tracing | 5.7 | 0.6 | 186.7 |
| 100 | metrics | 5.8 | 0.5 | 193.4 |
| 100 | logging | 9.7 | 0.8 | 538.9 |
| 1,000 | disabled | 849.8 | 1.3 | 826.1 |
| 1,000 | propagation | 123.2 | 1.2 | 818.1 |
| 1,000 | sampled-tracing | 132.3 | 0.9 | 817.4 |
| 1,000 | metrics | 144.9 | 2.5 | 877.8 |
| 1,000 | logging | 246.4 | 1.5 | 2,181.7 |

## Discovery with telemetry disabled

Times are P50 milliseconds for listing the complete catalog.

| Catalog | Transport | Tools | Resource templates | Prompts |
| ---: | --- | ---: | ---: | ---: |
| 1 | http | 0.3 | 0.2 | 0.2 |
| 1 | stdio | 0.1 | 0.1 | 0.1 |
| 100 | http | 6.9 | 0.7 | 1.1 |
| 100 | stdio | 7.0 | 0.6 | 0.9 |
| 1,000 | http | 16.8 | 1.2 | 2.4 |
| 1,000 | stdio | 22.1 | 1.2 | 2.3 |

## Warm operations at 4 KiB with telemetry disabled

Times are P50 milliseconds. A dash means that the catalog had no entry for that resource format.

| Catalog | Transport | Tool call | JSON resource | Text resource | Binary resource | Render prompt |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | http | 0.3 | 0.2 | — | — | 0.2 |
| 1 | stdio | 0.2 | 0.2 | — | — | 0.1 |
| 100 | http | 0.4 | 0.4 | 0.3 | 0.3 | 0.3 |
| 100 | stdio | 0.3 | 0.2 | 0.1 | 0.1 | 0.1 |
| 1,000 | http | 0.2 | 0.2 | 0.2 | 0.2 | 0.2 |
| 1,000 | stdio | 0.1 | 0.1 | 0.1 | 0.1 | 0.1 |

## Telemetry mode comparison

P50 milliseconds, 4-KiB tool calls, 100-entry catalog. Allocation is process-wide managed bytes per throughput operation; HTTP includes client and in-process server, stdio includes the parent client only.

| Telemetry | Transport | P50 (ms) | P95 (ms) | Throughput (ops/s) | Bytes/op | Recorded activities | Metrics | Logs |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| disabled | http | 0.4 | 0.5 | 2,238 | 181,819 | 0 | 0 | 0 |
| disabled | stdio | 0.3 | 0.3 | 3,794 | 48,547 | — | — | — |
| propagation | http | 0.3 | 0.5 | 3,438 | 185,923 | 0 | 0 | 0 |
| propagation | stdio | 0.1 | 0.2 | 6,580 | 49,582 | — | — | — |
| sampled-tracing | http | 0.1 | 0.1 | 7,975 | 186,496 | 135 | 0 | 0 |
| sampled-tracing | stdio | 0.1 | 0.2 | 6,544 | 49,731 | — | — | — |
| metrics | http | 0.1 | 0.3 | 6,127 | 180,066 | 0 | 270 | 0 |
| metrics | stdio | 0.1 | 0.2 | 7,092 | 48,570 | — | — | — |
| logging | http | 0.1 | 0.2 | 7,449 | 182,092 | 0 | 0 | 1,440 |
| logging | stdio | 0.4 | 1.5 | 1,644 | 48,742 | — | — | — |

## Application controls and MCP comparison

P50 microseconds for 4-KiB operations in the 100-entry, telemetry-disabled workload. The difference is an end-to-end control comparison and includes the client/transport; it is not an isolated server-only adapter measurement.

| Operation | Direct request bus | REST control | MCP HTTP | MCP stdio | HTTP minus REST | stdio minus REST |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| tool-call | 1.4 | 105.9 | 437.0 | 251.0 | 331.1 | 145.1 |
| read-resource-json | 1.4 | 105.9 | 351.1 | 193.8 | 245.2 | 87.9 |
| read-resource-text | 1.4 | 105.9 | 301.6 | 143.0 | 195.7 | 37.1 |
| read-resource-binary | 1.4 | 105.9 | 288.4 | 138.7 | 182.5 | 32.8 |
| render-prompt | 1.4 | 105.9 | 295.3 | 145.8 | 189.4 | 39.9 |

## Interpretation limits

The diagnostic command is a wiring check and is not a reported timing run. Allocation scope differs by transport as described above. Latency differences are descriptive; the campaign does not claim confidence intervals or an optimization result. MCP HTTP and stdio operation rows include SDK client, protocol, transport, Portia adapter, and handler work. Direct bus and REST rows are controls only.

## Reproduction

From the repository root, restore/build the benchmark project, then run:

```sh
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --mcp-protocol-qualification full docs/qualification/mcp-protocol-2026-11-11/raw.jsonl
```

The generated 1,000-tool catalog source is reproducible with `python3 bench/Portia.Benchmarks/Mcp/generate_tool_catalog.py`.

# MCP protocol performance baseline

- Captured: 2026-11-11T18:44:06.335061+00:00
- Source: `d165526d965c9a3bd7528384405cae7ce396bea2` (worktree status: `clean`)
- Rebased source counterpart: `1581a60`; the `bench/Portia.Benchmarks` tree (`0892f047`) and `src/Portia.Mcp` tree (`c363e765`) are identical to the measured source. Both builds enable the .NET 11 Runtime Async feature by default; the rebase adds only an explicit opt-out. The raw campaign remains attributed to the exact measured commit above.
- SDK/runtime: .NET SDK 11.0.100, .NET 11.0.0
- Environment: macOS 27.0.1, Arm64, 10 logical processors, server GC `False`
- MCP packages: `ModelContextProtocol` 2.4.0, `ModelContextProtocol.AspNetCore` 2.4.0
- Warm operations: 30 sequential latency samples after 10 warmups; throughput batches contain 50 operations. Startup and initialize are single observations per configuration.
- The raw JSONL contains 810 measurement rows, including 195 adapter-only rows; it retains raw latency samples, throughput duration, process allocation delta, telemetry counts, workload, and resolved packages.

## Adapter-only handler timing

P50 microseconds for 4-KiB results with telemetry disabled. These rows invoke the registered Portia tool adapter or resource/prompt handler directly, without MCP dispatch, JSON-RPC serialization, an MCP client, or transport.

| Catalog | Tool call | JSON resource | Text resource | Binary resource | Render prompt |
| --- | --- | --- | --- | --- | --- |
| 1 | 19.7 | 13.3 | — | — | 3.9 |
| 100 | 27.8 | 15.7 | 9.5 | 7.3 | 9.4 |
| 1000 | 11.2 | 11.6 | 18.8 | 18.0 | 9.5 |

Latency starts after the request context and the configured per-request service scope are created; it stops when the registered Portia adapter handler returns. Result validation runs after the timer. Throughput and process-allocation rows include per-call context and scope setup but exclude result validation and all MCP dispatch/client/transport work. The timed work includes Portia binding, request-bus dispatch, resource rendering or prompt rendering, and configured telemetry.

## Adapter telemetry comparison

Tool call, 4-KiB result, 100-entry catalog. P50/P95 are handler invocation microseconds; throughput and bytes/op include request-context and scope setup but exclude result validation, MCP dispatch, client, and transport.

Telemetry modes ran in matrix order within one process. The direct telemetry rows are descriptive; differences can include order and process-warmup effects and do not establish a causal telemetry cost.

| Telemetry | P50 (µs) | P95 (µs) | Ops/s | Bytes/op | Activities | Metrics | Logs |
| --- | --- | --- | --- | --- | --- | --- | --- |
| disabled | 27.8 | 63.8 | 37,288 | 51,211 | 0 | 0 | 0 |
| propagation | 7.0 | 7.9 | 34,459 | 51,565 | 0 | 0 | 0 |
| sampled-tracing | 7.1 | 8.1 | 43,910 | 51,900 | 18 | 0 | 0 |
| metrics | 8.7 | 9.1 | 38,226 | 50,763 | 0 | 270 | 0 |
| logging | 7.2 | 8.0 | 136,426 | 51,275 | 0 | 0 | 90 |

## Startup and protocol initialization

HTTP startup and initialize are separate. The SDK stdio client combines child-process startup and initialize, so it has one row. These are single observations; the first startup includes cold JIT effects, so treat them as lifecycle diagnostics.

| Catalog | Telemetry | HTTP startup (ms) | HTTP initialize (ms) | stdio startup + initialize (ms) |
| --- | --- | --- | --- | --- |
| 1 | disabled | 86.8 | 40.0 | 152.4 |
| 1 | propagation | 2.3 | 1.2 | 206.9 |
| 1 | sampled-tracing | 1.8 | 0.9 | 172.7 |
| 1 | metrics | 2.8 | 0.6 | 153.5 |
| 1 | logging | 3.6 | 0.7 | 155.4 |
| 100 | disabled | 64.3 | 0.9 | 239.7 |
| 100 | propagation | 7.0 | 0.6 | 205.0 |
| 100 | sampled-tracing | 7.1 | 0.7 | 205.6 |
| 100 | metrics | 7.3 | 0.6 | 214.6 |
| 100 | logging | 6.2 | 0.6 | 253.2 |
| 1,000 | disabled | 1,121.7 | 1.4 | 895.0 |
| 1,000 | propagation | 133.9 | 1.0 | 920.9 |
| 1,000 | sampled-tracing | 149.0 | 1.1 | 920.1 |
| 1,000 | metrics | 146.0 | 1.3 | 908.1 |
| 1,000 | logging | 143.3 | 0.8 | 965.0 |

## Discovery with telemetry disabled

P50 milliseconds for listing the complete catalog.

| Catalog | Transport | Tools | Resource templates | Prompts |
| --- | --- | --- | --- | --- |
| 1 | http | 0.3 | 0.2 | 0.2 |
| 1 | stdio | 0.2 | 0.1 | 0.1 |
| 100 | http | 8.5 | 0.8 | 1.2 |
| 100 | stdio | 6.5 | 0.6 | 1.0 |
| 1,000 | http | 21.4 | 1.4 | 2.2 |
| 1,000 | stdio | 28.8 | 1.1 | 1.9 |

## End-to-end operations at 4 KiB with telemetry disabled

P50 milliseconds. A dash means that the catalog did not contain that resource format.

| Catalog | Transport | Tool call | JSON resource | Text resource | Binary resource | Render prompt |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | http | 0.3 | 0.3 | — | — | 0.2 |
| 1 | stdio | 0.2 | 0.2 | — | — | 0.1 |
| 100 | http | 0.9 | 0.7 | 0.5 | 0.5 | 0.4 |
| 100 | stdio | 0.5 | 0.4 | 0.2 | 0.2 | 0.2 |
| 1,000 | http | 0.2 | 0.3 | 0.3 | 0.2 | 0.2 |
| 1,000 | stdio | 0.2 | 0.2 | 0.1 | 0.1 | 0.1 |

## End-to-end telemetry comparison

P50/P95 milliseconds, 4-KiB tool calls, 100-entry catalog. Allocation is process-wide managed bytes per throughput operation; HTTP includes client and in-process server, stdio measures the parent client process.

| Telemetry | Transport | P50 (ms) | P95 (ms) | Ops/s | Bytes/op | Activities | Metrics | Logs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| disabled | http | 0.90 | 1.25 | 1,237 | 181,461 | 0 | 0 | 0 |
| disabled | stdio | 0.46 | 0.58 | 2,085 | 48,648 | — | — | — |
| propagation | http | 0.14 | 0.22 | 5,978 | 186,380 | 0 | 0 | 0 |
| propagation | stdio | 0.17 | 0.26 | 4,617 | 49,697 | — | — | — |
| sampled-tracing | http | 0.16 | 0.25 | 5,194 | 186,836 | 134 | 0 | 0 |
| sampled-tracing | stdio | 0.24 | 0.29 | 3,942 | 49,966 | — | — | — |
| metrics | http | 0.16 | 0.25 | 5,067 | 180,353 | 0 | 270 | 0 |
| metrics | stdio | 0.20 | 0.35 | 4,533 | 48,676 | — | — | — |
| logging | http | 0.12 | 0.24 | 6,694 | 182,850 | 0 | 0 | 1440 |
| logging | stdio | 0.24 | 0.36 | 4,159 | 48,649 | — | — | — |

## Application controls and MCP comparison

P50 microseconds for 4-KiB operations in the 100-entry, telemetry-disabled workload. These end-to-end differences include SDK client and transport work; they are distinct from the adapter-only rows above.

| Operation | Direct request bus | REST control | MCP HTTP | MCP stdio | HTTP minus REST | stdio minus REST |
| --- | --- | --- | --- | --- | --- | --- |
| tool-call | 1.5 | 136.3 | 898.4 | 462.0 | 762.1 | 325.7 |
| read-resource-json | 1.5 | 136.3 | 715.0 | 410.3 | 578.7 | 274.0 |
| read-resource-text | 1.5 | 136.3 | 460.1 | 192.4 | 323.8 | 56.1 |
| read-resource-binary | 1.5 | 136.3 | 486.1 | 195.6 | 349.8 | 59.3 |
| render-prompt | 1.5 | 136.3 | 376.2 | 180.5 | 239.9 | 44.2 |

## Interpretation and limits

The complete workload covers Streamable HTTP, SDK-controlled stdio, catalog sizes 1/100/1,000, five telemetry modes, nested tool arguments, 256-byte/4-KiB/64-KiB results, JSON/text/binary resources, and rendered prompts. Catalog size 1 uses JSON resources; catalogs 100 and 1,000 include all three resource formats. No exporter is configured or awaited.

In this run, adapter-only P50 for 4-KiB results with telemetry disabled was 13.3/15.7/11.6 µs for JSON resource reads and 3.9/9.4/9.5 µs for prompt gets at catalog sizes 1/100/1,000. Earlier campaign values differed materially, so these single-run numbers do not establish a stable catalog-size trend or a material optimization opportunity. The direct rows isolate the handler path but still include request-bus/handler work and output construction. A paired before/after experiment is needed to assess the index change; the 30-sample distributions here are descriptive and claim no confidence interval or allocation reduction.

The HTTP and stdio measurements include SDK client, server dispatch, protocol, transport, Portia adapter, and handler work. Direct request-bus and REST rows are controls only. HTTP allocations cover both client and server; stdio allocations cover the parent process; adapter-only allocations cover the benchmark process during direct-call batches, including request context and service-scope setup. Use the raw samples when assessing tails or comparing later source changes.

## Reproduction

From the repository root, restore and build the benchmark project, then run:

```sh
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --mcp-protocol-qualification full docs/qualification/mcp-protocol-2026-11-11/raw.jsonl
```

The generated 1,000-tool catalog source is reproducible with `python3 bench/Portia.Benchmarks/Mcp/generate_tool_catalog.py`.

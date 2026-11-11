# MCP protocol performance baseline

- Captured: 2026-11-11T18:28:35.736458+00:00
- Source: `30b7410f1a3532c3fb59a9b913819520990b5d21` (worktree status: `clean`)
- SDK/runtime: .NET SDK 11.0.100, .NET 11.0.0
- Environment: macOS 27.0.1, Arm64, 10 logical processors, server GC `False`
- MCP packages: `ModelContextProtocol` 2.4.0, `ModelContextProtocol.AspNetCore` 2.4.0
- Warm operations: 30 sequential latency samples after 10 warmups; throughput batches contain 50 operations. Startup and initialize are single observations per configuration.
- The raw JSONL contains 810 measurement rows, including 195 adapter-only rows; it retains raw latency samples, throughput duration, process allocation delta, telemetry counts, workload, and resolved packages.

## Adapter-only handler timing

P50 microseconds for 4-KiB results with telemetry disabled. These rows invoke the registered Portia tool adapter or resource/prompt handler directly, without MCP dispatch, JSON-RPC serialization, an MCP client, or transport.

| Catalog | Tool call | JSON resource | Text resource | Binary resource | Render prompt |
| --- | --- | --- | --- | --- | --- |
| 1 | 21.3 | 12.1 | — | — | 4.2 |
| 100 | 35.9 | 23.8 | 10.0 | 7.2 | 9.1 |
| 1000 | 26.5 | 28.8 | 17.0 | 15.6 | 19.9 |

Latency starts after the request context and the configured per-request service scope are created; it stops when the registered Portia adapter handler returns. Result validation runs after the timer. Throughput and process-allocation rows include per-call context and scope setup but exclude result validation and all MCP dispatch/client/transport work. The timed work includes Portia binding, request-bus dispatch, resource rendering or prompt rendering, and configured telemetry.

## Adapter telemetry comparison

Tool call, 4-KiB result, 100-entry catalog. P50/P95 are handler invocation microseconds; throughput and bytes/op include request-context and scope setup but exclude result validation, MCP dispatch, client, and transport.

Telemetry modes ran in matrix order within one process. The direct telemetry rows are descriptive; differences can include order and process-warmup effects and do not establish a causal telemetry cost.

| Telemetry | P50 (µs) | P95 (µs) | Ops/s | Bytes/op | Activities | Metrics | Logs |
| --- | --- | --- | --- | --- | --- | --- | --- |
| disabled | 35.9 | 36.5 | 29,329 | 51,167 | 0 | 0 | 0 |
| propagation | 7.5 | 8.3 | 41,048 | 51,900 | 0 | 0 | 0 |
| sampled-tracing | 7.1 | 7.5 | 135,318 | 51,953 | 18 | 0 | 0 |
| metrics | 7.5 | 7.9 | 136,351 | 51,200 | 0 | 270 | 0 |
| logging | 8.6 | 9.3 | 28,547 | 50,981 | 0 | 0 | 90 |

## Startup and protocol initialization

HTTP startup and initialize are separate. The SDK stdio client combines child-process startup and initialize, so it has one row. These are single observations; the first startup includes cold JIT effects, so treat them as lifecycle diagnostics.

| Catalog | Telemetry | HTTP startup (ms) | HTTP initialize (ms) | stdio startup + initialize (ms) |
| --- | --- | --- | --- | --- |
| 1 | disabled | 70.1 | 31.8 | 141.6 |
| 1 | propagation | 3.1 | 1.0 | 151.4 |
| 1 | sampled-tracing | 1.8 | 0.7 | 137.1 |
| 1 | metrics | 2.6 | 0.7 | 166.4 |
| 1 | logging | 9.7 | 1.2 | 1,832.0 |
| 100 | disabled | 55.9 | 0.8 | 187.4 |
| 100 | propagation | 9.8 | 0.6 | 192.5 |
| 100 | sampled-tracing | 5.5 | 0.6 | 188.5 |
| 100 | metrics | 6.0 | 0.7 | 1,407.7 |
| 100 | logging | 16.5 | 1.0 | 1,136.4 |
| 1,000 | disabled | 874.7 | 1.2 | 857.9 |
| 1,000 | propagation | 121.0 | 0.7 | 837.8 |
| 1,000 | sampled-tracing | 119.3 | 1.1 | 832.2 |
| 1,000 | metrics | 877.6 | 1.2 | 2,481.8 |
| 1,000 | logging | 269.8 | 1.4 | 3,895.7 |

## Discovery with telemetry disabled

P50 milliseconds for listing the complete catalog.

| Catalog | Transport | Tools | Resource templates | Prompts |
| --- | --- | --- | --- | --- |
| 1 | http | 0.3 | 0.2 | 0.2 |
| 1 | stdio | 0.2 | 0.1 | 0.1 |
| 100 | http | 7.9 | 0.8 | 1.2 |
| 100 | stdio | 5.4 | 0.5 | 0.8 |
| 1,000 | http | 17.7 | 1.1 | 2.1 |
| 1,000 | stdio | 23.7 | 1.0 | 1.8 |

## End-to-end operations at 4 KiB with telemetry disabled

P50 milliseconds. A dash means that the catalog did not contain that resource format.

| Catalog | Transport | Tool call | JSON resource | Text resource | Binary resource | Render prompt |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | http | 0.3 | 0.3 | — | — | 0.2 |
| 1 | stdio | 0.2 | 0.2 | — | — | 0.1 |
| 100 | http | 0.4 | 0.3 | 0.3 | 0.3 | 0.3 |
| 100 | stdio | 0.3 | 0.3 | 0.1 | 0.1 | 0.1 |
| 1,000 | http | 0.2 | 0.2 | 0.2 | 0.2 | 0.2 |
| 1,000 | stdio | 0.1 | 0.1 | 0.1 | 0.1 | 0.1 |

## End-to-end telemetry comparison

P50/P95 milliseconds, 4-KiB tool calls, 100-entry catalog. Allocation is process-wide managed bytes per throughput operation; HTTP includes client and in-process server, stdio measures the parent client process.

| Telemetry | Transport | P50 (ms) | P95 (ms) | Ops/s | Bytes/op | Activities | Metrics | Logs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| disabled | http | 0.41 | 0.48 | 2,263 | 180,652 | 0 | 0 | 0 |
| disabled | stdio | 0.32 | 0.38 | 2,936 | 48,682 | — | — | — |
| propagation | http | 0.18 | 0.48 | 5,802 | 186,353 | 0 | 0 | 0 |
| propagation | stdio | 0.14 | 0.17 | 6,636 | 49,672 | — | — | — |
| sampled-tracing | http | 0.12 | 0.17 | 8,085 | 186,879 | 134 | 0 | 0 |
| sampled-tracing | stdio | 0.14 | 0.20 | 4,946 | 49,938 | — | — | — |
| metrics | http | 0.18 | 0.27 | 5,764 | 180,264 | 0 | 270 | 0 |
| metrics | stdio | 0.45 | 0.99 | 316 | 48,521 | — | — | — |
| logging | http | 0.33 | 1.08 | 1,439 | 182,509 | 0 | 0 | 1440 |
| logging | stdio | 0.45 | 1.55 | 1,562 | 48,504 | — | — | — |

## Application controls and MCP comparison

P50 microseconds for 4-KiB operations in the 100-entry, telemetry-disabled workload. These end-to-end differences include SDK client and transport work; they are distinct from the adapter-only rows above.

| Operation | Direct request bus | REST control | MCP HTTP | MCP stdio | HTTP minus REST | stdio minus REST |
| --- | --- | --- | --- | --- | --- | --- |
| tool-call | 1.4 | 113.2 | 405.6 | 323.9 | 292.4 | 210.7 |
| read-resource-json | 1.4 | 113.2 | 336.1 | 272.4 | 222.9 | 159.2 |
| read-resource-text | 1.4 | 113.2 | 281.6 | 119.4 | 168.4 | 6.2 |
| read-resource-binary | 1.4 | 113.2 | 284.7 | 122.0 | 171.5 | 8.8 |
| render-prompt | 1.4 | 113.2 | 283.1 | 127.2 | 169.9 | 14.0 |

## Interpretation and limits

The complete workload covers Streamable HTTP, SDK-controlled stdio, catalog sizes 1/100/1,000, five telemetry modes, nested tool arguments, 256-byte/4-KiB/64-KiB results, JSON/text/binary resources, and rendered prompts. Catalog size 1 uses JSON resources; catalogs 100 and 1,000 include all three resource formats. No exporter is configured or awaited.

The adapter-only rows identify catalog-size-dependent Portia lookup costs: for 4-KiB results with telemetry disabled, JSON resource-read P50 rises from 12.1 µs at catalog 1 to 28.8 µs at 1,000; prompt-get P50 rises from 4.2 µs to 19.9 µs. These direct rows isolate the handler path but include request-bus/handler work and output construction. They identify linear entry lookup as a concrete candidate for an M6 paired experiment; they do not establish that an optimization is material. The 30-sample distributions are descriptive, and no before/after confidence interval or allocation reduction is claimed.

The HTTP and stdio measurements include SDK client, server dispatch, protocol, transport, Portia adapter, and handler work. Direct request-bus and REST rows are controls only. HTTP allocations cover both client and server; stdio allocations cover the parent process; adapter-only allocations cover the benchmark process during direct-call batches, including request context and service-scope setup. Use the raw samples when assessing tails or comparing later source changes.

## Reproduction

From the repository root, restore and build the benchmark project, then run:

```sh
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --mcp-protocol-qualification full docs/qualification/mcp-protocol-2026-11-11/raw.jsonl
```

The generated 1,000-tool catalog source is reproducible with `python3 bench/Portia.Benchmarks/Mcp/generate_tool_catalog.py`.

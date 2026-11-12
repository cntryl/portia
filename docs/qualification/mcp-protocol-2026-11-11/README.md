# MCP protocol performance baseline

- Captured: 2026-11-11T20:00:28.197115+00:00
- Benchmark source commit: `c47b8997c293a84893f5deae705e1b23f36bdd22` (worktree status: `clean`)
- Final production source: `src/Portia.Mcp` tree `dcfa4715f2aede74df0a604d98c8f1138d643c4b`, identical to current main commit `4f066f38da0d3a2bbf406b8225af35c44ae7c24b` and the earlier merged main commit `d8412cc5bc280af2a96b29c87833a364a2365b1e` (#197). Merges #198 and #199 changed no MCP production files. The benchmark-only branch adds the harness/report; it does not change the measured MCP production tree.
- Benchmark project tree: `0892f047138e30c6e67d16323d1962bc553498d8`
- SDK/runtime: .NET SDK 11.0.100, .NET 11.0.0
- Environment: macOS 27.0.1, Arm64, 10 logical processors, server GC `False`
- MCP packages: `ModelContextProtocol` 2.4.0, `ModelContextProtocol.AspNetCore` 2.4.0
- Warm operations: 30 sequential latency samples after 10 warmups; throughput batches contain 50 operations. Startup and initialize are single observations per configuration.
- The raw JSONL contains 810 measurement rows, including 195 adapter-only rows, plus metadata and a completion record. It retains raw latency samples, throughput duration, process allocation delta, telemetry counts, workload, and resolved packages.

## Adapter-only handler timing

P50 microseconds for 4-KiB results with telemetry disabled. These rows invoke the registered Portia tool adapter or resource/prompt handler directly, without MCP dispatch, JSON-RPC serialization, an MCP client, or transport.

| Catalog | Tool call | JSON resource | Text resource | Binary resource | Render prompt |
| --- | --- | --- | --- | --- | --- |
| 1 | 19.8 | 11.6 | — | — | 3.8 |
| 100 | 17.8 | 13.3 | 6.1 | 5.9 | 5.2 |
| 1,000 | 9.5 | 10.7 | 7.9 | 6.9 | 7.2 |

Latency starts after the request context and configured per-request service scope are created; it stops when the registered Portia adapter handler returns. Result validation runs after the timer. Throughput and process-allocation rows include per-call context and scope setup but exclude result validation and all MCP dispatch/client/transport work. Timed work includes Portia binding, request-bus dispatch, resource or prompt rendering, and configured telemetry.

## Adapter telemetry comparison

Tool call, 4-KiB result, 100-entry catalog. P50/P95 are handler invocation microseconds; throughput and bytes/op include request-context and scope setup but exclude result validation, MCP dispatch, client, and transport.

Telemetry modes ran in matrix order within one process. These rows are descriptive; differences can include order and process-warmup effects and do not establish causal telemetry cost.

| Telemetry | P50 (µs) | P95 (µs) | Ops/s | Bytes/op | Activities | Metrics | Logs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| disabled | 17.8 | 18.6 | 55,679 | 51,250 | 0 | 0 | 0 |
| propagation | 7.1 | 8.2 | 136,426 | 51,856 | 0 | 0 | 0 |
| sampled-tracing | 7.2 | 7.9 | 39,008 | 51,726 | 18 | 0 | 0 |
| metrics | 6.8 | 18.8 | 144,592 | 51,200 | 0 | 270 | 0 |
| logging | 6.9 | 9.6 | 149,835 | 51,200 | 0 | 0 | 90 |

## Startup and protocol initialization

HTTP startup and initialize are separate. The SDK stdio client combines child-process startup and initialize, so it has one row. These are single observations; the first startup includes cold JIT effects, so treat them as lifecycle diagnostics.

| Catalog | Telemetry | HTTP startup (ms) | HTTP initialize (ms) | stdio startup + initialize (ms) |
| --- | --- | ---: | ---: | ---: |
| 1 | disabled | 73.0 | 34.2 | 150.6 |
| 1 | propagation | 2.2 | 1.0 | 141.8 |
| 1 | sampled-tracing | 1.7 | 0.8 | 138.5 |
| 1 | metrics | 2.3 | 0.5 | 137.4 |
| 1 | logging | 2.7 | 0.8 | 143.5 |
| 100 | disabled | 54.6 | 1.0 | 198.6 |
| 100 | propagation | 5.7 | 0.6 | 188.6 |
| 100 | sampled-tracing | 6.5 | 0.5 | 189.5 |
| 100 | metrics | 6.0 | 0.5 | 188.8 |
| 100 | logging | 6.3 | 0.6 | 189.5 |
| 1,000 | disabled | 836.4 | 1.7 | 864.9 |
| 1,000 | propagation | 113.5 | 0.7 | 822.6 |
| 1,000 | sampled-tracing | 121.2 | 0.7 | 823.1 |
| 1,000 | metrics | 126.4 | 0.9 | 812.3 |
| 1,000 | logging | 116.7 | 0.8 | 821.0 |

## Discovery with telemetry disabled

P50 milliseconds for listing the complete catalog.

| Catalog | Transport | Tools | Resource templates | Prompts |
| --- | --- | ---: | ---: | ---: |
| 1 | http | 0.3 | 0.2 | 0.2 |
| 1 | stdio | 0.2 | 0.1 | 0.1 |
| 100 | http | 8.0 | 0.7 | 1.1 |
| 100 | stdio | 8.0 | 0.5 | 0.9 |
| 1,000 | http | 12.9 | 1.2 | 1.9 |
| 1,000 | stdio | 22.7 | 1.1 | 1.9 |

## End-to-end operations at 4 KiB with telemetry disabled

P50 milliseconds. A dash means that the catalog did not contain that resource format.

| Catalog | Transport | Tool call | JSON resource | Text resource | Binary resource | Render prompt |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | http | 0.3 | 0.3 | — | — | 0.2 |
| 1 | stdio | 0.2 | 0.2 | — | — | 0.1 |
| 100 | http | 0.6 | 0.5 | 0.3 | 0.3 | 0.3 |
| 100 | stdio | 0.4 | 0.3 | 0.1 | 0.1 | 0.2 |
| 1,000 | http | 0.2 | 0.2 | 0.2 | 0.2 | 0.2 |
| 1,000 | stdio | 0.1 | 0.1 | 0.1 | 0.1 | 0.1 |

## End-to-end telemetry comparison

P50/P95 milliseconds for 4-KiB tool calls with a 100-entry catalog. Allocation is process-wide managed bytes per throughput operation; HTTP includes client and in-process server, while stdio measures the parent client process.

| Telemetry | Transport | P50 (ms) | P95 (ms) | Ops/s | Bytes/op | Activities | Metrics | Logs |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| disabled | http | 0.57 | 0.63 | 1,727 | 181,120 | 0 | 0 | 0 |
| disabled | stdio | 0.39 | 0.56 | 2,331 | 48,765 | — | — | — |
| propagation | http | 0.12 | 0.15 | 8,160 | 186,475 | 0 | 0 | 0 |
| propagation | stdio | 0.13 | 0.17 | 6,844 | 49,759 | — | — | — |
| sampled-tracing | http | 0.12 | 0.13 | 7,634 | 187,352 | 135 | 0 | 0 |
| sampled-tracing | stdio | 0.13 | 0.15 | 6,660 | 49,837 | — | — | — |
| metrics | http | 0.15 | 0.29 | 6,869 | 180,316 | 0 | 270 | 0 |
| metrics | stdio | 0.13 | 0.16 | 7,294 | 48,736 | — | — | — |
| logging | http | 0.12 | 0.15 | 7,760 | 182,920 | 0 | 0 | 1,440 |
| logging | stdio | 0.13 | 0.16 | 7,461 | 48,776 | — | — | — |

## Application controls and MCP comparison

P50 microseconds for 4-KiB operations in the 100-entry, telemetry-disabled workload. The direct request-bus and REST-control values are controls; MCP rows include SDK client and transport work.

| Operation | Direct request bus | REST control | MCP HTTP | MCP stdio | HTTP minus REST | stdio minus REST |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| tool-call | 1.2 | 112.6 | 572.4 | 392.3 | 459.8 | 279.7 |
| read-resource-json | 1.2 | 112.6 | 484.7 | 251.7 | 372.1 | 139.1 |
| read-resource-text | 1.2 | 112.6 | 310.0 | 143.1 | 197.4 | 30.5 |
| read-resource-binary | 1.2 | 112.6 | 313.6 | 133.9 | 201.0 | 21.3 |
| render-prompt | 1.2 | 112.6 | 321.9 | 178.7 | 209.3 | 66.1 |

## Interpretation and limits

The workload covers loopback Streamable HTTP, SDK-controlled stdio, direct request-bus and REST controls, catalog sizes 1/100/1,000, five telemetry modes, nested tool arguments, 256-byte/4-KiB/64-KiB results, JSON/text/binary resources, and prompts. Catalog size 1 uses JSON resources; catalogs 100 and 1,000 include all three formats. No exporter is configured or awaited. The raw artifact records all 810 measurements, their sequential samples and throughput batches, telemetry counts, workload plan, dependencies, runtime, environment, and source provenance.

Adapter-only rows call the registered Portia tool or resource/prompt handler directly from the server's registered options. They exclude MCP dispatch, JSON-RPC, SDK client, and transport; each call's request context and service scope are prepared before timing, and result validation runs after timing. These measurements isolate the Portia adapter invocation within the stated boundary. The campaign is a single run with 30 sequential latency samples per warmed configuration; use the raw samples when inspecting tails, and use paired runs for optimization claims. Values vary across single runs, so the summary does not claim a stable catalog-size trend or causal telemetry effect.

HTTP/stdio measurements include SDK client, server dispatch, protocol, transport, Portia adapter, and handler work. HTTP allocations cover client plus in-process server; stdio allocations cover the parent process. Throughput and allocations use a separate 50-operation batch. Direct request-bus and REST rows are controls, not server-only or adapter-only timings.

## Untimed end-to-end payload verification

The timed HTTP/stdio client calls in the captured campaign checked response success and content shape, but did not assert returned payload lengths. Adapter-only results were checked after their timing intervals. A separate fast qualification mode now checks exact UTF-8 byte lengths for 256-byte, 4-KiB, and 64-KiB tool results, JSON resource data, text resources, and rendered prompts, plus exact decoded byte lengths for binary resources, through both Streamable HTTP and stdio. These assertions run outside all timing intervals and emit no benchmark rows. This follow-up gate does not modify or retroactively verify the historical raw timing rows.

Run the repeatable payload correctness gate with:

```sh
dotnet run --configuration Release --project bench/Portia.Benchmarks -- \
  --mcp-protocol-payload-qualification
```

CI is configured to run this gate after building the benchmark project.

## Reproduction

From the repository root, restore and build the benchmark project, then run:

```sh
dotnet run --configuration Release --project bench/Portia.Benchmarks -- \
  --mcp-protocol-qualification full docs/qualification/mcp-protocol-2026-11-11/raw.jsonl
```

The generated 1,000-tool catalog source is reproducible with `python3 bench/Portia.Benchmarks/Mcp/generate_tool_catalog.py`.

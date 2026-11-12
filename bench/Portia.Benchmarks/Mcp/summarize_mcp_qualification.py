#!/usr/bin/env python3
"""Writes the human-readable report beside one MCP qualification JSONL campaign."""

import json
import sys
from pathlib import Path


def number(value, digits=1):
    return "—" if value is None else f"{value:,.{digits}f}"


def row_for(rows, transport, catalog, mode, operation, payload=0):
    return next(
        row
        for row in rows
        if row.get("recordType") == "measurement"
        and row.get("transport") == transport
        and row.get("catalogSize") == catalog
        and row.get("telemetryMode") == mode
        and row.get("operation") == operation
        and row.get("payloadBytes") == payload
    )


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: summarize_mcp_qualification.py <raw-results.jsonl>")
    raw_path = Path(sys.argv[1]).resolve()
    records = [json.loads(line) for line in raw_path.read_text(encoding="utf-8").splitlines()]
    metadata = next(record for record in records if record.get("recordType") == "metadata")
    measurements = [record for record in records if record.get("recordType") == "measurement"]
    dependencies = metadata["dependencies"]

    report = [
        "# MCP protocol performance baseline",
        "",
        f"- Captured: {metadata['capturedUtc']}",
        f"- Source: `{metadata['sourceCommit']}` (worktree status: `{metadata['sourceTreeStatus'] or 'clean'}`)",
        f"- SDK/runtime: .NET SDK {metadata['dotnetSdk']}, {metadata['framework']} ({metadata['runtime']})",
        f"- Environment: {metadata['operatingSystem']}, {metadata['architecture']}, "
        f"{metadata['processorCount']} logical processors, server GC `{metadata['serverGc']}`",
        f"- MCP packages: `ModelContextProtocol` {dependencies['ModelContextProtocol']}, "
        f"`ModelContextProtocol.AspNetCore` {dependencies['ModelContextProtocol.AspNetCore']}",
        f"- Warm operations: {metadata['plan']['Samples']} sequential latency samples after "
        f"{metadata['plan']['Warmups']} warmups; throughput batches contain "
        f"{metadata['plan']['ThroughputIterations']} operations. Startup and initialize are "
        "single observations per configuration.",
        "- The raw JSONL records every latency sample, throughput duration, process allocation "
        "delta, telemetry count, workload setting, and resolved package version.",
        "",
        "## Startup and protocol initialization",
        "",
        "HTTP startup and initialize are separate. The SDK stdio client combines child-process "
        "startup and initialize, so it has one row. Startup/initialize numbers are single "
        "observations; the first application startup includes cold JIT effects, so use them only "
        "as lifecycle diagnostics.",
        "",
        "| Catalog | Telemetry | HTTP startup (ms) | HTTP initialize (ms) | stdio startup + initialize (ms) |",
        "| ---: | --- | ---: | ---: | ---: |",
    ]
    for catalog in (1, 100, 1000):
        for mode in ("disabled", "propagation", "sampled-tracing", "metrics", "logging"):
            http_start = row_for(measurements, "http", catalog, mode, "startup")
            http_init = row_for(measurements, "http", catalog, mode, "negotiation")
            stdio = row_for(measurements, "stdio", catalog, mode, "startup-and-negotiation")
            report.append(
                f"| {catalog:,} | {mode} | {number(http_start['p50Microseconds'] / 1000)} | "
                f"{number(http_init['p50Microseconds'] / 1000)} | "
                f"{number(stdio['p50Microseconds'] / 1000)} |"
            )

    report.extend(
        [
            "",
            "## Discovery with telemetry disabled",
            "",
            "Times are P50 milliseconds for listing the complete catalog.",
            "",
            "| Catalog | Transport | Tools | Resource templates | Prompts |",
            "| ---: | --- | ---: | ---: | ---: |",
        ]
    )
    for catalog in (1, 100, 1000):
        for transport in ("http", "stdio"):
            values = [
                row_for(measurements, transport, catalog, "disabled", operation)["p50Microseconds"] / 1000
                for operation in ("discover-tools", "discover-resources", "discover-prompts")
            ]
            report.append(
                f"| {catalog:,} | {transport} | " + " | ".join(number(value) for value in values) + " |"
            )

    report.extend(
        [
            "",
            "## Warm operations at 4 KiB with telemetry disabled",
            "",
            "Times are P50 milliseconds. A dash means that the catalog had no entry for that resource format.",
            "",
            "| Catalog | Transport | Tool call | JSON resource | Text resource | Binary resource | Render prompt |",
            "| ---: | --- | ---: | ---: | ---: | ---: | ---: |",
        ]
    )
    for catalog in (1, 100, 1000):
        for transport in ("http", "stdio"):
            operations = [
                "tool-call",
                "read-resource-json",
                "read-resource-text",
                "read-resource-binary",
                "render-prompt",
            ]
            cells = []
            for operation in operations:
                match = next(
                    (
                        row
                        for row in measurements
                        if row.get("transport") == transport
                        and row.get("catalogSize") == catalog
                        and row.get("telemetryMode") == "disabled"
                        and row.get("operation") == operation
                        and row.get("payloadBytes") == 4096
                    ),
                    None,
                )
                cells.append(number(match["p50Microseconds"] / 1000) if match else "—")
            report.append(f"| {catalog:,} | {transport} | " + " | ".join(cells) + " |")

    report.extend(
        [
            "",
            "## Telemetry mode comparison",
            "",
            "P50 milliseconds, 4-KiB tool calls, 100-entry catalog. Allocation is process-wide "
            "managed bytes per throughput operation; HTTP includes client and in-process server, "
            "stdio includes the parent client only.",
            "",
            "| Telemetry | Transport | P50 (ms) | P95 (ms) | Throughput (ops/s) | Bytes/op | Recorded activities | Metrics | Logs |",
            "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
        ]
    )
    for mode in ("disabled", "propagation", "sampled-tracing", "metrics", "logging"):
        for transport in ("http", "stdio"):
            row = row_for(measurements, transport, 100, mode, "tool-call", 4096)
            counts = row.get("telemetryCounts") or {}
            report.append(
                f"| {mode} | {transport} | {number(row['p50Microseconds'] / 1000)} | "
                f"{number(row['p95Microseconds'] / 1000)} | "
                f"{number(row['throughputOperationsPerSecond'], 0)} | "
                f"{number(row['processAllocatedBytesPerOperation'], 0)} | "
                f"{number(counts.get('RecordedActivities'), 0)} | "
                f"{number(counts.get('Measurements'), 0)} | {number(counts.get('LogEntries'), 0)} |"
            )

    report.extend(
        [
            "",
            "## Application controls and MCP comparison",
            "",
            "P50 microseconds for 4-KiB operations in the 100-entry, telemetry-disabled workload. "
            "The difference is an end-to-end control comparison and includes the client/transport; "
            "it is not an isolated server-only adapter measurement.",
            "",
            "| Operation | Direct request bus | REST control | MCP HTTP | MCP stdio | HTTP minus REST | stdio minus REST |",
            "| --- | ---: | ---: | ---: | ---: | ---: | ---: |",
        ]
    )
    direct = row_for(measurements, "direct", 100, "disabled", "request-bus", 4096)["p50Microseconds"]
    rest = row_for(measurements, "http", 100, "disabled", "rest-control", 4096)["p50Microseconds"]
    for operation in ("tool-call", "read-resource-json", "read-resource-text", "read-resource-binary", "render-prompt"):
        try:
            http = row_for(measurements, "http", 100, "disabled", operation, 4096)["p50Microseconds"]
            stdio = row_for(measurements, "stdio", 100, "disabled", operation, 4096)["p50Microseconds"]
        except StopIteration:
            continue
        report.append(
            f"| {operation} | {number(direct)} | {number(rest)} | {number(http)} | {number(stdio)} | "
            f"{number(http - rest)} | {number(stdio - rest)} |"
        )

    report.extend(
        [
            "",
            "## Interpretation limits",
            "",
            "The diagnostic command is a wiring check and is not a reported timing run. Allocation "
            "scope differs by transport as described above. Latency differences are descriptive; "
            "the campaign does not claim confidence intervals or an optimization result. MCP HTTP "
            "and stdio operation rows include SDK client, protocol, transport, Portia adapter, and "
            "handler work. Direct bus and REST rows are controls only.",
            "",
            "## Untimed payload correctness gate",
            "",
            "The timed HTTP/stdio calls check response success and content shape; they do not assert "
            "returned payload lengths. A separate fast gate verifies exact 256-byte, 4-KiB, and "
            "64-KiB tool results, JSON resource data, text resources, binary resources, and rendered "
            "prompts through both real transports. Its checks run outside timing intervals and do "
            "not add measurement rows or retroactively validate a captured timing artifact.",
            "",
            "Run the correctness gate independently with:",
            "",
            "```sh",
            "dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \\",
            "  --mcp-protocol-payload-qualification",
            "```",
            "",
            "## Reproduction",
            "",
            "From the repository root, restore/build the benchmark project, then run:",
            "",
            "```sh",
            "dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \\",
            f"  --mcp-protocol-qualification full {raw_path.relative_to(Path.cwd()) if raw_path.is_relative_to(Path.cwd()) else raw_path}",
            "```",
            "",
            "The generated 1,000-tool catalog source is reproducible with "
            "`python3 bench/Portia.Benchmarks/Mcp/generate_tool_catalog.py`.",
            "",
        ]
    )
    report_path = raw_path.with_name("README.md")
    report_path.write_text("\n".join(report), encoding="utf-8")
    print(report_path)


if __name__ == "__main__":
    main()

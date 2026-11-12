#!/usr/bin/env python3
"""Validate and summarize the M5 MCP lookup-position supplement."""

import hashlib
import json
import math
import sys
from pathlib import Path


CATALOG_SIZES = (1, 100, 1000)
TRANSPORTS = ("http", "stdio")
OPERATIONS = (
    "read-resource-fixed-json",
    "read-resource-template-first-json",
    "read-resource-template-last-json",
    "get-prompt-first",
    "get-prompt-last",
)
EXPECTED_MCP_TREE = "dcfa4715f2aede74df0a604d98c8f1138d643c4b"
BENCHMARK_PATCH_COMMIT = "950544b805c3e8b91d61f4a4245b88a11d396a9b"


def fail(message):
    raise SystemExit(message)


def fmt(value):
    return f"{value:,.1f}"


def main():
    if len(sys.argv) != 2:
        fail("usage: summarize_mcp_lookup_supplement.py <raw-results.jsonl>")

    raw_path = Path(sys.argv[1]).resolve()
    try:
        raw_bytes = raw_path.read_bytes()
        records = [json.loads(line) for line in raw_bytes.decode("utf-8").splitlines() if line.strip()]
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        fail(f"cannot read valid JSONL from {raw_path}: {error}")

    metadata = [row for row in records if row.get("recordType") == "metadata"]
    measurements = [row for row in records if row.get("recordType") == "measurement"]
    complete = [row for row in records if row.get("recordType") == "complete"]
    if len(metadata) != 1 or len(complete) != 1 or len(measurements) != 30 or len(records) != 32:
        fail("expected one metadata row, 30 measurements, and one completion row")

    metadata = metadata[0]
    complete = complete[0]
    if metadata.get("runMode") != "lookup-position-supplement":
        fail("input is not an M5 lookup-position supplement")
    if metadata.get("mcpSourceTree") != EXPECTED_MCP_TREE:
        fail("input does not use the documented pre-optimization MCP source tree")
    if metadata.get("sourceTreeStatus"):
        fail("input source worktree was not clean")
    if metadata.get("plan") != {"Warmups": 10, "Samples": 30, "ThroughputIterations": 50}:
        fail("input does not use the documented measurement plan")
    if complete.get("mode") != "lookup-position-supplement" or complete.get("configurations") != 30:
        fail("completion record does not cover all 30 configurations")

    expected = {
        (transport, catalog, operation)
        for transport in TRANSPORTS
        for catalog in CATALOG_SIZES
        for operation in OPERATIONS
    }
    indexed = {}
    for row in measurements:
        key = (row.get("transport"), row.get("catalogSize"), row.get("operation"))
        if key not in expected or key in indexed:
            fail(f"unexpected or duplicate measurement row: {key}")
        samples = row.get("rawLatencyMicroseconds")
        if (
            row.get("telemetryMode") != "disabled"
            or row.get("payloadBytes") != 4096
            or row.get("sampleCount") != 30
            or not isinstance(samples, list)
            or len(samples) != 30
            or not all(isinstance(value, (int, float)) and math.isfinite(value) and value > 0 for value in samples)
        ):
            fail(f"measurement row has incomplete workload or samples: {key}")
        indexed[key] = row
    if set(indexed) != expected:
        fail("measurement rows do not cover the full transport/catalog/operation matrix")

    deps = metadata["dependencies"]
    final_template_vs_first = []
    fixed_resource_vs_first = []
    for transport in TRANSPORTS:
        first_template = indexed[(transport, 1000, "read-resource-template-first-json")]
        final_template = indexed[(transport, 1000, "read-resource-template-last-json")]
        fixed = indexed[(transport, 1000, "read-resource-fixed-json")]
        final_template_vs_first.append(
            f"{transport} {fmt(final_template['p50Microseconds'])} vs "
            f"{fmt(first_template['p50Microseconds'])} µs"
        )
        fixed_resource_vs_first.append(
            f"{transport} {fmt(fixed['p50Microseconds'])} vs "
            f"{fmt(first_template['p50Microseconds'])} µs"
        )
    position_observation = (
        "At catalog size 1, first and final positions refer to the same entry. At size 1,000, "
        "the final-template P50 is higher than the first-template P50 for both transports "
        f"({'; '.join(final_template_vs_first)}). The fixed resource, registered after all "
        f"templates, also measured higher than the first-template control "
        f"({'; '.join(fixed_resource_vs_first)}). "
        "First/final prompt client measurements are similar in this single capture; they do not "
        "independently establish an end-to-end prompt slowdown."
    )
    report = [
        "# M5 MCP lookup-position supplement",
        "",
        "This extends the pre-optimization M5 workload with fixed-resource, final-template, and final-prompt lookups. "
        "It is a single descriptive capture, not a paired optimization claim.",
        "",
        f"- Captured: `{metadata['capturedUtc']}`",
        f"- Benchmark source commit: `{metadata['sourceCommit']}` (clean worktree)",
        f"- Benchmark-only patch commit for reproduction: `{BENCHMARK_PATCH_COMMIT}`",
        f"- M5 baseline commit: `3525d305b84d90da3bd8cb36ac688606f01d8088`; MCP source tree: `{metadata['mcpSourceTree']}`",
        f"- Benchmark project tree: `{metadata['benchmarkProjectTree']}`",
        f"- Artifact SHA-256: `{hashlib.sha256(raw_bytes).hexdigest()}`",
        f"- SDK/runtime: .NET SDK `{metadata['dotnetSdk']}`, `{metadata['framework']}` ({metadata['runtime']})",
        f"- Environment: {metadata['operatingSystem']}, {metadata['architecture']}, "
        f"{metadata['processorCount']} logical processors, server GC `{metadata.get('serverGc', 'not recorded')}`",
        f"- MCP packages: `ModelContextProtocol` {deps['ModelContextProtocol']}, "
        f"`ModelContextProtocol.AspNetCore` {deps['ModelContextProtocol.AspNetCore']}",
        "- Workload: Streamable HTTP over loopback Kestrel and official SDK stdio child; catalogs 1/100/1,000; "
        "4-KiB JSON payloads; telemetry disabled.",
        "- Each configuration has 10 warmups, 30 sequential latency samples, and a separate 50-call throughput "
        "and allocation batch. Startup and MCP initialization occur outside latency samples.",
        "",
        "## M5 baseline measurements",
        "",
        "P50/P95 are microseconds. HTTP allocations include client and in-process server; stdio allocations cover "
        "the parent client process. All 30 raw rows and individual samples are in [raw.jsonl](raw.jsonl).",
        "",
        "| Catalog | Transport | Fixed resource P50/P95 | First template P50/P95 | Final template P50/P95 | "
        "First prompt P50/P95 | Final prompt P50/P95 |",
        "| ---: | --- | ---: | ---: | ---: | ---: | ---: |",
    ]
    for catalog in CATALOG_SIZES:
        for transport in TRANSPORTS:
            values = []
            for operation in OPERATIONS:
                row = indexed[(transport, catalog, operation)]
                values.append(f"{fmt(row['p50Microseconds'])} / {fmt(row['p95Microseconds'])}")
            report.append(f"| {catalog:,} | {transport} | " + " | ".join(values) + " |")

    report.extend(
        [
            "",
            position_observation,
            "",
            "## Link to the paired M6 comparison",
            "",
            "The optimization decision and paired direct-handler results are in "
            "[m6-decision.md](../mcp-protocol-2026-11-11/m6-decision.md), with the 10-pair raw data in "
            "[m6-lookup-paired.jsonl](../mcp-protocol-2026-11-11/m6-lookup-paired.jsonl). That comparison uses "
            "500 latency samples after 100 warmups and a 1,000-call allocation/throughput batch; its paired "
            "95% intervals for the final resource-template, fixed-resource, and final-prompt lookups at catalog "
            "sizes 100 and 1,000 are below zero. The M5 supplement establishes that the expensive lookup "
            "positions are present in the baseline workload; the paired M6 artifact establishes the measured "
            "candidate improvement. The two captures have distinct stated timing scopes and are not pooled.",
            "",
            "## Reproduction",
            "",
            f"Apply benchmark-only commit `{BENCHMARK_PATCH_COMMIT}` to the documented baseline "
            "commit `3525d305b84d90da3bd8cb36ac688606f01d8088`, then run:",
            "",
            "```sh",
            "dotnet restore bench/Portia.Benchmarks/Portia.Benchmarks.csproj",
            "dotnet build --configuration Release --no-restore bench/Portia.Benchmarks/Portia.Benchmarks.csproj",
            "dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \\",
            "  --mcp-protocol-qualification lookup-position-supplement <output.jsonl>",
            "python3 bench/Portia.Benchmarks/Mcp/summarize_mcp_lookup_supplement.py <output.jsonl>",
            "```",
            "",
            "The raw output records the exact source and benchmark trees, dependency input hashes, resolved package "
            "versions, workload, runtime, operating system, processor architecture, and every sample. The "
            "supplement omits other M5 telemetry modes, payload sizes, text/binary resources, tool calls, startup, "
            "discovery, and direct/REST controls; it does not replace the full M5 campaign.",
            "",
        ]
    )

    report_path = raw_path.with_name("README.md")
    report_path.write_text("\n".join(report), encoding="utf-8")
    print(report_path)


if __name__ == "__main__":
    main()

# M6 measured MCP lookup optimization

## Decision

Use an exact URI index for fixed resources and an ordinal prompt-name index for prompt gets. Keep
resource templates in registration order. Their matcher reuses pre-split template segments,
validates each request URI once, and allocates captured values only after the literal path segments
match. This preserves visibility and cancellation behavior while removing repeated scans and
allocations from exact lookups and nonmatching templates.

The refreshed paired measurements show a material improvement for last-entry lookups at catalog
sizes 100 and 1,000. The former no-change decision is superseded. Low-search controls do not show
a material regression.

## Paired evidence

The raw evidence is in [m6-lookup-paired.jsonl](m6-lookup-paired.jsonl). It contains ten alternating
before/after pairs, 500 latency samples after 100 warmups, and a separate 1,000-call throughput and
allocation batch for each operation and catalog size. The baseline source is `729c9a03667cbec2ac9ee4b5c12ba80c1e475f07`; the candidate is `01030e984267a9443617f4a844abdeeb23a94baa`. Their MCP source trees are recorded in the artifact. The benchmark project tree is identical (`c635709a735f149369116342565fb15b3149b1bb`). Both use .NET 11.0.0, the same package assets, ModelContextProtocol 2.4.0, and the same Arm64 machine.

P50, P95, and allocations are medians of the ten per-run values. The paired 95% intervals use the
ten per-run P50 differences (candidate minus baseline), a paired Student t interval with 9 degrees
of freedom and critical value 2.262. Negative intervals favor the candidate.

| Catalog | Adapter lookup | P50 µs before → after | P95 µs before → after | Bytes/op before → after | Paired 95% CI for P50 delta (µs) |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Fixed resource | 11.75 → 10.73 | 14.00 → 13.04 | 24,633 → 23,121 (-6.1%) | [-1.10, -0.86] |
| 1 | Final template | 11.42 → 11.04 | 12.92 → 13.27 | 24,191 → 23,552 (-2.6%) | [-0.52, -0.32] |
| 1 | First prompt control | 3.71 → 3.67 | 4.13 → 4.02 | 15,671 → 15,577 (-0.6%) | [-0.15, -0.02] |
| 1 | Final prompt | 3.65 → 3.65 | 3.98 → 3.90 | 15,671 → 15,574 (-0.6%) | [-0.10, 0.05] |
| 100 | Fixed resource | 53.08 → 10.77 | 59.67 → 12.52 | 103,050 → 23,129 (-77.6%) | [-43.11, -41.97] |
| 100 | Final template | 57.90 → 14.33 | 65.92 → 16.58 | 110,520 → 23,552 (-78.7%) | [-66.42, -38.73] |
| 100 | First prompt control | 4.54 → 3.67 | 4.96 → 3.94 | 16,465 → 15,576 (-5.4%) | [-1.23, -0.75] |
| 100 | Final prompt | 5.08 → 3.65 | 5.48 → 3.92 | 16,465 → 15,579 (-5.4%) | [-2.44, -1.17] |
| 1,000 | Fixed resource | 489.02 → 10.88 | 708.83 → 13.13 | 815,853 → 23,130 (-97.2%) | [-799.76, -433.80] |
| 1,000 | Final template | 725.96 → 37.50 | 867.85 → 43.50 | 895,261 → 23,552 (-97.4%) | [-821.95, -420.37] |
| 1,000 | First prompt control | 6.19 → 3.73 | 7.21 → 4.06 | 23,557 → 15,573 (-33.9%) | [-4.24, -1.98] |
| 1,000 | Final prompt | 13.71 → 3.75 | 14.40 → 4.00 | 23,552 → 15,571 (-33.9%) | [-15.68, -8.01] |

Every allocation comparison improved. All four last-entry operations at sizes 100 and 1,000 have
paired P50 intervals fully below zero. The first-prompt control also improves as the catalog grows.
At size 1, the final-prompt interval crosses zero, while its P95 and allocation medians are slightly
lower. The final-template P95 median at size 1 rises by 0.35 microseconds while its P50 interval is
favorable and allocation is lower; no measured control shows a material regression.

## Correctness and limits

- Fixed-resource lookup uses the exact URI dictionary. Prompt lookup uses the ordinal name
  dictionary. Template reads visit templates in registration order and retain the existing
  per-entry visibility checks.
- Actor resolution and visibility still run with the configured deadline token. Cancellation,
  result bounds, telemetry outcome recording, and uniform unavailable errors remain in the
  handler path.
- The MCP test project passed 153/153 tests on the candidate, including the index regression for
  fixed-resource and prompt lookups, template order, and exact/template reads.
- The CI describes paired campaign-level P50 values. P95 samples are retained and summarized, but
  this report does not claim a separate P95 confidence interval.

The targeted campaign is reproducible from the repository root after a Release build:

```sh
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --mcp-adapter-lookup-qualification /tmp/m6-lookup.jsonl
```

Issue #142 is addressed by this measured optimization; the pull request can close it when merged.

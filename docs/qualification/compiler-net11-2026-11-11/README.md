# .NET 11 compiler and analyzer qualification

This campaign measures the combined compiler workload before and after the .NET 11 / Portia
0.11.0 upgrade. It runs all eight Portia generators and five analyzers with the same current
benchmark harness, on macOS 27.0.1 Arm64 with Roslyn 5.9.0 and the .NET 11.0.0 runtime.
Each compiler edit has 20 samples. The generated consumer compiles in both runs.

The workload has 1,000 commands, 10 feature assemblies, 10 JSON contexts, 100 HTTP mappings, and
10,000 unrelated types. Each run exercises unchanged, unrelated body, call-site whitespace, event
add/remove/rename, route change, and referenced-feature replacement cases. Source, generated output,
environment, package lock, analyzer/generator resolution, raw samples, and build logs are retained
in the `baseline` and `current` directories. The workload source hash is identical across both
runs. Generated hashes differ because the generator implementation changed.

## Compiler results

P50 latency and median allocation results for the combined generator driver:

| Case | Baseline P50 | Current P50 | Baseline allocation | Current allocation |
|---|---:|---:|---:|---:|
| Cold driver | 297.9 ms | 290.8 ms | 236.2 MB | 245.2 MB |
| Unchanged | 58.5 ms | 58.4 ms | 56.5 MB | 54.1 MB |
| Unrelated body edit | 254.8 ms | 229.2 ms | 133.5 MB | 124.8 MB |
| Call-site whitespace | 418.0 ms | 413.7 ms | 132.8 MB | 126.2 MB |
| Event add | 208.7 ms | 224.0 ms | 234.1 MB | 241.0 MB |
| Event remove | 416.8 ms | 335.8 ms | 144.0 MB | 148.0 MB |
| Event rename | 272.9 ms | 346.6 ms | 141.5 MB | 145.8 MB |
| Route change | 175.0 ms | 173.8 ms | 132.4 MB | 125.8 MB |
| Referenced feature replacement | 163.0 ms | 162.5 ms | 123.2 MB | 115.1 MB |

The event-add and event-rename medians rose; event-remove and unrelated-body medians fell. These
20-sample medians are descriptive, not a 95% confidence claim. The retained raw samples show broad
overlap in several cases. The current generated output is 16,037,711 bytes and compiles; the
baseline output is 14,979,016 bytes and compiles.

The five analyzer P50s ranged from 1.78 to 2.40 seconds in the current run. All five were slightly
faster than the baseline medians, while analyzer allocations rose by about 1.7% to 3.1%. Full
per-analyzer samples are in `summary.json` and `raw.csv`.

Actual consumer-build controls passed. They used two samples per build mode, so treat those as
diagnostic: the cold build P50 was 9.43 seconds on the baseline and 12.32 seconds currently; the
unchanged build P50 was 625 ms and 585 ms, respectively.

## Baseline construction

Baseline Portia source is commit `7d7e3c4`. The baseline campaign uses the current benchmark harness
from `41a36b4`; `baseline-harness.patch` applies that harness and the SDK 11 compatibility settings
to the source commit. The baseline project still targets `net10.0`. The benchmark process ran on
.NET 11 by setting `DOTNET_ROLL_FORWARD=Major`. SDK 11's newly enforced built-in style analyzers
were disabled for the historical baseline build only; Portia analyzers remain measured by the
campaign itself. The exact normalized project files and package lock are included with the raw
results. The baseline composite source is commit `d94d17d`.

To reproduce the baseline, check out `7d7e3c4`, apply `baseline-harness.patch`, then run:

```sh
DOTNET_ROLL_FORWARD=Major scripts/compiler-campaign.sh /tmp/portia-compiler-baseline 20 combined 2
```

To reproduce the current run, check out the commit recorded in `current/source-commit.txt` and run:

```sh
scripts/compiler-campaign.sh /tmp/portia-compiler-current 20 combined 2
```

## Remaining qualification

This campaign establishes compiler and analyzer behavior on one macOS Arm64 host. It does not
qualify runtime hot paths or Linux NativeAOT. Hosted CI on the exact .NET 11 change and MCP
description branch passed build, test, packed-consumer, and NativeAOT lanes; retain the CI checks
as the platform qualification evidence. Generated-code ergonomics and a full runtime regression
campaign remain open under issue #175.

# Runtime Async comparison (#175 partial)

This is a bounded comparison of Portia's .NET 11 application code with Runtime Async enabled
and disabled on the same installed .NET runtime. It covers request dispatch, generated HTTP JSON
endpoints, and processor dispatch. It is one runtime-performance slice of #175; it does not
complete the generator/analyzer qualification or the issue's other acceptance criteria.

Runtime Async remains a preview feature. This comparison is diagnostic and makes no production
adoption recommendation. The [official .NET 11 runtime notes](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime#runtime-async)
document the feature and the `UseRuntimeAsync=false` per-project opt-out. The repository build
target now honors that opt-out while retaining its existing .NET 11 default.

## Method

Run `scripts/runtime-async-comparison.sh <absolute-output-directory>` from a clean checkout. The
script builds the same Release sources twice into separate output directories. It sets
`UseRuntimeAsync=false` for the off build and `UseRuntimeAsync=true` for the on build, disables
incremental compilation, records the SDK and runtime, and checks the Roslyn command lines. The
benchmark assembly and its Portia project references differ in compiler feature mode; both use
the same preinstalled .NET 11 shared framework and NuGet dependency set. The framework assemblies
themselves are prebuilt with Runtime Async enabled in both modes.

The script first runs the existing reflection-disabled runtime qualification controls in both
modes. It then runs BenchmarkDotNet ShortRun with one launch, three warmups, and three measured
iterations for each case:

| Suite | Cases |
| --- | ---: |
| `RequestDispatchBenchmarks` | 4 |
| `HttpLifecycleBenchmarks.Json` | 12 (transport, payload size, and content-length combinations) |
| `ProcessorDispatchBenchmarks` | 6 (single and batch, three event counts) |

BenchmarkDotNet 0.15.8's out-of-process toolchain fails on this `net11.0` target with
`GetRuntimeVersion not implemented for NotRecognized`. The campaign therefore uses BenchmarkDotNet's
in-process toolchain, with the off and on runs in separate `dotnet` processes. The benchmark logs
also record that the host denied BenchmarkDotNet's request to raise process priority. These limits
make this a diagnostic comparison rather than a release performance qualification.

## Result

Both modes passed the runtime qualification controls and ran all 22 benchmark cases on
`.NET 11.0.0 (11.0.0, 11.0.26.53018)`, Arm64 RyuJIT, macOS 27.0.1, Apple M5, using SDK 11.0.100.
The off build's Roslyn invocations contain no `runtime-async=on` switch; the on build's nine
`.NET 11` project compilations contain it. The benchmark reports identify the same runtime and
BenchmarkDotNet in-process toolchain for every case.

All 22 pairs of BenchmarkDotNet 99.9% timing confidence intervals overlap. The samples do not
support a performance improvement or regression claim. Keep #175 open; a follow-up should use an
out-of-process BenchmarkDotNet toolchain that recognizes `net11.0`, multiple independent launches,
and a longer sample campaign before making a performance conclusion.

The six CSV reports summarize the results. The six full JSON reports retain the per-iteration
samples and host metadata. They are grouped by compiler mode under [`raw-samples`](raw-samples/);
the captured SDK and runtime details are under [`environment`](environment/). The benchmark and
Portia core assemblies have different hashes across modes, while the benchmark runtime config and
dependency manifest match; see [`environment/build-hashes.txt`](environment/build-hashes.txt). The
measured source revision is recorded in [`environment/source-commit.txt`](environment/source-commit.txt).

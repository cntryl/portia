# Compiler benchmarks

This executable verifies and measures the current Portia compiler tooling. It implements the first
measurement slice of the [benchmark design](../../docs/compiler-benchmarks.md). Timing reports are
diagnostic comparisons; they do not establish the complete qualification requested by issue #175.

Build and verify from the repository root:

```sh
dotnet build bench/Portia.CompilerBenchmarks --configuration Release
dotnet bench/Portia.CompilerBenchmarks/bin/Release/net11.0/Portia.CompilerBenchmarks.dll list
dotnet bench/Portia.CompilerBenchmarks/bin/Release/net11.0/Portia.CompilerBenchmarks.dll verify --size small
```

Verification covers the shipped eight generators, five analyzers and three code-fix providers.
The manifest currently has 93 cases: seven representative application requests, generator edits and isolated generators, analyzer compilation
and document requests, nine code-fix actions with six stages each, and eight project Fix All cases.
The newer contract metadata path has a description edit and a `PORTIA031` activation control.
The representative graph uses valid Common/Core/Api/Worker projects, compilation references, correct
project convention options and no diagnostic controls. Its body/metadata/member edits include
generation and changed-document syntax/semantic diagnostics; full graph diagnostics and consumer
emission are checked after timing. `applications/all/full` measures generation plus full graph
diagnostics; `applications/all/analyze` starts from generated input.
Generator verification checks clean/incremental output parity, compilation of combined consumers,
declared output-family changes, and stable tracked model steps. Code fixes use actual diagnostics,
map portable locations to workspace trees, apply real operations and compile all affected projects.
They must remove every targeted diagnostic. Small Fix All cases repair ten diagnostics; Standard
and Large repair 100. The namespace fixture scales references to 1, 10 and 100 respectively.

Run A/A calibration using the same immutable executable on the macOS reference host:

```sh
dotnet bench/Portia.CompilerBenchmarks/bin/Release/net11.0/Portia.CompilerBenchmarks.dll host
dotnet bench/Portia.CompilerBenchmarks/bin/Release/net11.0/Portia.CompilerBenchmarks.dll calibrate \
  --case generators/all/body --size small --pairs 10 --samples 10 --warmup 3 --seconds 300 \
  --output artifacts/compiler/calibration
```

Calibration requires at least ten process pairs and 100 operations per side. Both confidence bounds
must lie within the declared positive/negative margin for median, P95 and allocations. An apparent
speedup from drift also fails A/A. Warmup uses independent compilations of the measured size. A
calibration verdict applies to its case, dimensions and recorded host; it does not qualify the full
compiler matrix. Exit 2 means collection completed but calibration was inconclusive; exit 1 means
collection or host acceptance failed.

The controller requires a ten-second idle preflight and a one-second quiet interval before each
worker. It samples host CPU/memory/thermal/power state while each managed worker runs, subtracting
worker and controller CPU from observed host activity. Background CPU over 10%, increased swap,
memory/thermal pressure, low-power mode or unavailable required telemetry stop collection. Rejected
observations and already completed samples remain in the raw log. Linux supports counter inspection
and correctness verification; thermal telemetry is currently unavailable there, so it cannot pass
this reference-host timing policy. SDK subprocess workloads need separate process-tree accounting.
The strict timing policy currently covers managed Roslyn workers.

Run a bounded diagnostic A/A comparison using two processes per pair:

```sh
dotnet bench/Portia.CompilerBenchmarks/bin/Release/net11.0/Portia.CompilerBenchmarks.dll run \
  --case generators/all/body --size small --pairs 5 --samples 10 --seconds 300 \
  --output artifacts/compiler/aa
```

Each operation gets fresh compilation/driver/workspace state. Edit cases start from a populated
baseline generator driver. Compiler metadata references are reused within a worker. Code-fix
discovery starts after the diagnostic has bound the project, as it does when a real diagnostic
drives a light bulb; `discover-cached` intentionally primes the same-project context lookup.
`cold` means creating a new driver in a warm worker. It is not process or machine cold start.
`complete` measures discovery, preview, change computation, workspace application and diagnostic
refresh, starting from an already available diagnostic. Other stages refresh and compile after
their timers. Consumer emit validation, hashing, serialization and tracing stay outside isolated
timers; Roslyn binding needed to perform the measured operation remains included.

For A/B, publish each revision's harness to a separate immutable directory. Both must contain the
same harness/fixture protocol and use the same SDK/runtime/Roslyn. Freeze application/framework
references from the baseline so rebuilding unrelated assemblies cannot change compiler input:

```sh
dotnet artifacts/compiler/baseline/Portia.CompilerBenchmarks.dll references \
  --output artifacts/compiler/references.json
export PORTIA_COMPILER_REFERENCES="$PWD/artifacts/compiler/references.json"
dotnet artifacts/compiler/candidate/Portia.CompilerBenchmarks.dll compare \
  --baseline artifacts/compiler/baseline/Portia.CompilerBenchmarks.dll \
  --candidate artifacts/compiler/candidate/Portia.CompilerBenchmarks.dll \
  --case generators/all/body --size standard --pairs 10 --samples 20 --seconds 1800 \
  --output artifacts/compiler/comparison
```

The default JSON generator comes from the explicitly pinned .NET 11.0.0 reference pack alongside
the runtime installation. Set `PORTIA_JSON_GENERATOR` to an explicit DLL path for another approved
reference pack. Every worker records its actual hash, Roslyn/runtime/OS/architecture, component
DLL hashes, a fingerprint of the harness IL, reference hash and fixture hash. Mismatched inputs, unequal outputs and
incomplete
pairs cannot produce a compatible comparison. Intentionally changed generated output needs review
and a new comparison contract; the current protocol requires identical output hashes across revisions.
The current report protocol is `portia-compiler-benchmarks-3`; prior reports are rejected.
Workers also record the warm-up count and fixture size, and comparisons reject different warm-up
configurations. The harness accepts only its current report protocol.

Raw JSONL uses a new filename per invocation. Completed samples are flushed while the child runs;
timeouts and nonzero worker exits produce an incomplete summary and exit 1. The controller alternates
AB/BA order, runs workers sequentially, and enforces the total time budget. Median comparisons use
process-pair bootstrap intervals and the proposed percentage/absolute margins. Fewer than five
pairs are explicitly insufficient. Process lifetime includes verification and setup; it is not an
operation latency. Per-sample latency, CPU, process allocation and GC counts have their own fields.
A complete diagnostic comparison exits 0 even when a metric reports regression or inconclusive;
that exit code confirms collection and verification, not performance acceptance.

The harness rejects reuse of an independently prepared operation. Its tests also reject missing
components/actions, stale/overbroad output changes and nonfinite comparisons, and verify allocations
made on asynchronous worker threads. Ordinary CI runs correctness verification, not timing gates.

The remaining design work includes more reference and metadata variations, runs without System.Text.Json, long edit-session retention, in-flight cancellation
latency, first-load/build measurements, profiling orchestration, and broader IDE workload coverage. Analyzer workloads currently include explicit
diagnostic controls and generated/prebound input; their totals are not a clean application's IDE
latency. Until those remaining paths are implemented, use these results to investigate
individual operations and retain the broader qualification as open.

A [VS Code host pilot](vscode/README.md) now covers diagnostic feedback and the partial-processor
code fix in both Automatic and Balanced generator execution modes. Broader editor completion
and workload qualification remain as specified in the design.

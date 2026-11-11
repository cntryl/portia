# Compiler output identity and invalidation

This qualification adds a reproducible cross-version snapshot for the `combined` compiler fixture
used by [the .NET 11 compiler campaign](../compiler-net11-2026-11-11/README.md). It checks source
identity, generated output hashes, generated consumer compilation, event catalog registrations, JSON
root/context pairs, and expected per-edit output invalidation.

## Scope and result

The fixture has 1,000 commands, 1,001 source events including `ProcessorEvent`, 10 feature
assemblies, 10 JSON contexts, 100 HTTP mappings, and 10,000 unrelated types. It records these edits:

- unchanged input and unrelated method-body edit
- call-site whitespace and route changes
- event add, remove, and rename
- replacement of a referenced feature assembly whose source API stays the same

Each version generates and compiles the consumer for every edit. The harness compares all emitted
files by generator name, hint name, UTF-8 byte count, and SHA-256. It also parses every generated
domain-event registration into a `(type, discriminator, version)` tuple and every generated JSON
root into a `(root type, context type)` pair. Both generated sets must match their source model and
each other across versions. Those two contracts pass for all eight edits.

On the unchanged fixture, 2,062 of the 2,066 output keys shared by the baseline and current
generators have identical byte hashes. Four shared keys have different contents, one baseline key
is absent in current output, and current output has 38 additional keys. These differences are
explained by the generator source changes below. The evidence does not claim byte identity for every
generated file or semantic equivalence for every generator.

| Edit | Baseline added / removed / changed | Current added / removed / changed |
|---|---:|---:|
| unchanged | 0 / 0 / 0 | 0 / 0 / 0 |
| unrelated body | 0 / 0 / 0 | 0 / 0 / 0 |
| call-site whitespace | 0 / 0 / 2 | 0 / 0 / 2 |
| event add | 10 / 0 / 13 | 10 / 0 / 13 |
| event remove | 0 / 1 / 4 | 0 / 1 / 4 |
| event rename | 1 / 1 / 4 | 1 / 1 / 6 |
| route change | 0 / 0 / 2 | 0 / 0 / 2 |
| referenced feature replacement | 0 / 0 / 0 | 0 / 0 / 0 |

Unrelated body edits and implementation-only feature replacement leave generated files unchanged.
Call-site and route edits change registration/HTTP interceptor outputs. Event edits change only
domain-event catalog, JSON-root, registration-call, and System.Text.Json generated outputs. The
event-rename changed-file count differs because the current generator partitions output differently;
both versions produce the same source-matched event and JSON-root identities.

## The four shared-key changes

The generated source snapshots and generator source snapshots are retained in the checked-in
[`raw-evidence.tar.gz`](raw-evidence.tar.gz). The cross-version inventory is available directly in
[`cross-version.json`](cross-version.json).
For the unchanged case, the four common keys with different byte hashes are:

1. `DomainEventTypeCatalogGeneratedExtensions.g.cs` changed from an inline list of 1,001
   `Register<T>` calls to a short forwarding helper. The current generator emits the registration
   statements through `GeneratedSource.StatementTree` buckets; see
   `DomainEventCatalogGenerator.GenerateCatalog` and `GeneratedSource.StatementTree`. The harness
   extracts every registration from all generated catalog files and verifies its exact type,
   discriminator, and version against source.
2. `BenchmarkProjector_…EventDispatcher.g.cs` and
   `BenchmarkReactor_…EventDispatcher.g.cs` were reformatted with explicit braces and indentation.
   The current emitter writes those blocks in `ProjectorReactorEventDispatcherGenerator.Generate`.
   The topology attribute, override, event case, handler call, context construction, and default
   branch are retained; both generated consumers compile.
3. `PortiaGeneratedRegistrationInterceptors.g.cs` changed from the monolithic interceptor file to a
   short shared-registration helper. Current code emits compilation-wide registration blocks once
   through `GeneratedSource.StatementTree` and emits interception methods by calling type in
   `RegistrationCallInterceptorGenerator.Generate`. The other per-calling-type outputs are present
   in the current snapshot and the generated consumer compiles.

The 38 current-only keys are 17 domain-event catalog shards, 19 shared/registration interceptor
files, and 2 HTTP binding/interceptor files. The single baseline-only key is the former
`PortiaGeneratedHttpBinding.g.cs` file, which current output splits. Exact keys and hashes are in
`cross-version.json` and each version's `manifest.json`.

## Runtime and historical build note

Both campaigns ran under .NET 11.0.0 and Roslyn 5.9.0. The baseline Portia source is `7d7e3c4` and
targets `net10.0`; `DOTNET_ROLL_FORWARD=Major` runs its harness on .NET 11. The current source is
`dcee7a0bb67bc7546b77ac0b9e4636c1f05ed037`. Both runs use the same .NET 11
`System.Text.Json.SourceGeneration.dll`; its binary hash is recorded and checked.

A fresh analyzer-enabled build of the historical baseline fails with Roslyn `RS0025` duplicate
public API diagnostics. The historical build reports 1,101 distinct `RS0025` diagnostics (the log
contains each diagnostic once during build output and once in the failure summary). For example:

```text
error RS0025: The symbol 'Cntryl.Portia.RequestHttpBindingGenerator' appears more than once in the public API files
error RS0025: The symbol 'static Cntryl.Portia.ProcessingObservation.InvokeAsync<TEvent>(string! handlerName, Cntryl.Portia.IProjectorHandler<TEvent!>! handler, Cntryl.Portia.IProjectorContext<TEvent!>! context, System.Threading.CancellationToken ct) -> System.Threading.Tasks.ValueTask<Cntryl.Portia.Result>' appears more than once in the public API files
```

The full failure is preserved in `baseline/analyzer-enabled-build-failure.log`; the command exits
with status 1 and the summary is `1,101 Error(s)`. The harness-only follow-up build succeeds using
`-p:RunAnalyzers=false`. That flag is scoped to building the historical `Portia.CompilerBenchmarks`
project so it can invoke generators directly; it does not skip building either historical Portia
generator project. The baseline run records the loaded `Portia.Generators` and
`Portia.AspNetCore.Generators` assembly SHA-256 hashes in `baseline/manifest.json`; these match the
historical DLLs copied into `assemblies/baseline/`. The runtime harness invokes those loaded
assemblies. The current build uses analyzers normally. This comparison does not qualify analyzer
performance.

The new `src/Portia.Generators/Infrastructure/GeneratedSource.cs` utility does not exist at the
historical revision. `source/baseline/missing-files.txt` records that absence; the raw snapshots
include the historical generator files that do exist and the current utility.

## Reproduction

Run the full campaign in a fresh output directory:

```sh
bash scripts/compiler-output-identity-campaign.sh /tmp/portia-output-identity
```

The script creates and removes a temporary historical worktree, preserves the fixture sources,
generator source and assembly snapshots, records raw generated source by content hash, verifies all
hashes and invalidation rules, and writes `raw-evidence.tar.gz`. The checked-in evidence was captured
against the current commit above. Each manifest records the benchmark harness source hash; the
verifier and campaign script sources are retained in the evidence archive.

Issue #175 remains open. This slice establishes no runtime performance result, NativeAOT result, or
generated-code ergonomics result, and it does not make a global semantic-equivalence claim for all
generators.

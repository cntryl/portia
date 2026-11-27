# Compiler tooling benchmark design

Qualify Portia's analyzers, code fixes, and source generators by measuring the work a developer
causes: opening a project, editing a file, requesting a fix, previewing it, and applying it.
Use focused measurements to find a cause and complete compiler workloads to check the benefit.
This is the proposed benchmark contract for [issue #175](https://github.com/cntryl/portia/issues/175).
The separate [Runtime Async comparison](https://github.com/cntryl/portia/issues/257) remains paused.

Implement a small maintained `bench/Portia.CompilerBenchmarks` executable with deterministic
fixtures and a process controller. Keep fixture definitions, correctness expectations, and budgets
in source control; publish raw runs as CI or release artifacts. The budgets and complete qualification
requirements below remain proposed; the initial executable implements the subset described next.

The initial executable and its current coverage are documented in
[Compiler benchmarks](../bench/Portia.CompilerBenchmarks/README.md). Its diagnostic reports keep
the remaining host, workload and qualification requirements explicit.

The implementation now includes a valid four-project application graph, captured Accounts SDK
inputs, strict host monitoring and A/A calibration, process-first-request notification, retention
and cancellation probes, and a two-project VS Code replay. These are implemented measurement paths;
their existence does not satisfy the qualification criteria below. Reports reject prior protocols
and incompatible harness bytes. Ordinary CI verifies correctness without accepting timings.

The November 18 reference-host calibration attempt stopped at its idle preflight: background CPU
was 18.2%, above the declared 10% limit. No measurement worker ran. Controlled timing campaigns and
the expanded editor replay await an idle-host window; implementation and correctness checks can
continue independently. The next evidence sequence is accepted-host A/A, representative warm and
first-load comparisons, long retention and in-flight cancellation observations, then Automatic and
Balanced editor replay. Profile missed budgets, change only confirmed causes, and repeat matched
measurements before publishing an acceptance matrix. More reference/metadata variants, code-fix
cache retention, cancellation retry checks and SDK build timing remain explicit gaps in the
full design.

## Measurement boundaries

Use three kinds of run. They share inputs and expected results, but have different instrumentation.

| Run | Question | Instrumentation |
|---|---|---|
| Verify | Did the operation do the required work and invalidate the right outputs? | Expected diagnostics and actions, generated source hashes, consumer compilation, tracked generator steps |
| Measure | How long did that operation take and how much did it allocate? | Wall time, process CPU time, process allocation delta, GC collection deltas; tracking and profilers disabled |
| Profile | Which code or allocation caused the measured cost? | EventPipe CPU/allocation traces and Roslyn telemetry; report separately from qualification timing |

The controller builds once, then launches one worker at a time. A worker loads only one candidate
revision, fixture, and scenario. Do not load baseline and candidate into the same process: static
caches and assembly identity would contaminate the comparison. Run both binaries through the same
harness and fixture revision, with the same SDK, Roslyn, framework references, and options.

The current repository pins SDK 11.0.100, Roslyn packages 5.12.0, and BenchmarkDotNet 0.15.8.
Record the actual loaded DLL hashes as well as these declared versions. Package Roslyn and SDK
compiler Roslyn are different hosts; report their results separately. Begin with a directly
launched worker for Roslyn operations. BenchmarkDotNet can support later steady helper benchmarks
after a .NET 11 toolchain preflight. Its throughput pilot must never repeat a stateful edit or fix
until it becomes a cached no-op. [BenchmarkDotNet describes the different run strategies here.](https://benchmarkdotnet.org/articles/guides/choosing-run-strategy.html)

## Inventory and activation

Every shipped component needs a valid input, a rejection or diagnostic input, and an irrelevant
input. Enumerate the actual generator, analyzer, and code-fix types from the built assemblies and
fail verification when the coverage manifest omits a type. Also map supported diagnostics and
action equivalence keys to scenarios; counting types alone misses paths inside a provider.

| Source generator | Required coverage |
|---|---|
| `DomainEventCatalogGenerator` | Local/referenced events, version/upcaster checks, catalog call locations, empty and duplicate registrations |
| `JsonMetadataDiagnosticGenerator` | Local/referenced root coverage, missing metadata, transport/dispatch roots, irrelevant matching method names |
| `JsonRootMarkerGenerator` | Root advertisement, context validation, nested/shared contract graphs, description/validation metadata, `PORTIA031` ownership checks |
| `PortiaServiceRegistrationGenerator` | Request/transport discovery, shape and accessibility errors, routing/discriminator conflicts, output partitioning |
| `ProjectorReactorEventDispatcherGenerator` | Projector/reactor, single/batch handlers, partial/nested declarations, invalid handler shapes |
| `RegistrationCallInterceptorGenerator` | Every supported registration/dispatch family, local/referenced events and contexts, call-site changes, unsupported forwarding |
| `RequestHttpBindingGenerator` | Shared/distinct binding shapes, HTTP call locations, property/constructor binding, custom binders, validation and converter metadata |
| `RequestShapeDiagnosticsGenerator` | Valid and invalid component class declarations, generic/nested shapes, unrelated base lists and partial classes |

| Analyzer | Fixture that proves it is active |
|---|---|
| `ApplicationStructureAnalyzer` | Correct and incorrect paths/namespaces, aggregate collaborators and ownership, multi-type files, aggregate review budget |
| `ApplicationBoundaryAnalyzer` | References, feature registration ownership, handler stubs, query/reactor hydration, unrelated invocations and constructors |
| `ApplicationContractAnalyzer` | Common contracts, behavior inside contracts, schema versioning, model/value/event forms |
| `ComponentPracticeAnalyzer` | Component dependencies, dispatch/publication patterns, catch/return paths, unrelated methods |
| `ScenarioObservationAnalyzer` | Retained/awaited and discarded scenario results, expression bodies, misleading `Expect` method names |
| `AggregateOnHandlerAnalyzer` | An explicit throw inside a registered aggregate replay handler |

Application convention cases must set `PortiaProjectStructure`, project name/directory,
root namespace, and `IsTestProject` correctly. Include Common, Core, Api, Worker, test,
unsupported-role, and opt-out variants. Run generated sources and disabled/suppressed diagnostics
as explicit controls. An empty diagnostic list does not prove an enabled analyzer ran.

## Fixtures and scale

Use ordinary files distributed across feature folders. Store the source seed and every dimension;
record the resulting documents, syntax nodes, declarations, references, and source/generated bytes.
One giant source string hides costs tied to project and document count.

| Fixture | Initial dimensions | Purpose |
|---|---|---|
| Minimal | One active component or diagnostic; 1–5 documents | Attribute cost to one behavior |
| Small application | 20 requests, 20 events, 10 processors, 10 HTTP mappings, 2 JSON contexts, 100 unrelated types | Common interactive workload |
| Standard application | 200 requests, 200 events, 50 processors, 40 HTTP mappings, 5 contexts, 2,000 unrelated types | Primary qualification workload |
| Large application | 1,000 requests, 1,000 events, 200 processors, 100 HTTP mappings, 10 contexts, 10,000 unrelated types | Scale and stress |
| Accounts | The checked-in application, captured with project references and compiler options | Real composition and generated-consumer build |
| Negative controls | No Portia reference; Portia referenced but unused; unrelated attributes and same-named APIs | Cost of doing no useful Portia work |

For Standard, start with 10 features and a four-project Common/Core/Api/Worker solution plus
tests. Derive source files from the model with one primary type per file; do not independently
inflate every axis. Larger referenced-feature graphs are dedicated experiments.

Vary one dimension at a time at 1, 10, 100, and 1,000 relevant items where feasible: unrelated
documents, invocation count, attribute count, aggregates and collaborator references, HTTP
call sites versus unique shapes, JSON roots versus contexts, graph width/depth, and referencing
projects. Include shared/cyclic contract types, inheritance, aliases/global usings, nested types,
and metadata references with no relevant exports. Sweep partition boundaries at 127/128/129
entries. Include malformed code and dense diagnostics as separate cases from valid applications.
Use the Small/Standard/Large combinations for realistic totals, not a Cartesian product of axes.

## Generator operations and edits

Measure each generator in isolation for attribution and the shipped combination for qualification.
Run the combined case both with and without the pinned System.Text.Json generator to expose
interaction costs. Verify Portia-generated output with the dependencies it needs; an isolated
diagnostic or generator run is not necessarily a compilable application.

| Operation | State and timed work |
|---|---|
| First request in a new process | Worker launch through first completed request; separately mark assembly/reference load, fixture construction, and Roslyn execution |
| New driver in a warm process | Fresh input compilation and driver; time driver creation and `RunGenerators`; no prior binding/diagnostic validation of that measured compilation |
| Unchanged | Populate a driver outside timing, then time one run on the identical input compilation |
| Independent edit | Populate a fresh baseline driver, apply one edit preserving all other syntax trees, then time exactly one run on the edited input |
| Edit session | Retain each returned driver and compilation through a fixed edit/undo sequence; measure each request and cumulative cost |

Use the original user compilation as driver input. Never feed a compilation containing previous
generated trees back into the next generator run. Prepare tree replacement outside the isolated
generator timer; include incremental parsing and project update in the complete edit-request timer.
Use `SourceText.WithChanges`/`SyntaxTree.WithChangedText` for the editor path. A complete reparse
is a separate control.

Each independent repetition constructs a new measured world, including a new edited compilation.
Repeating `RunGenerators` on the already edited driver measures unchanged work. Even reusing the
same edited compilation with a baseline driver can warm semantic caches; do not use that shortcut.
JIT warmup uses a disjoint fixture. First-use static-cache behavior belongs in the new-process run.

| Edit | Required expectation |
|---|---|
| Unrelated method body; comment in a separate file | No generated source content changes; measure semantic rebinding and downstream work |
| Leading whitespace or `#line` moving a call site | Location-bearing interceptors may change; location-independent implementations stay byte-identical |
| Add/remove/rename an event; change version/upcaster | Exactly the declared catalog, registration, metadata, and diagnostic effects |
| Change a request route, member, or handler interface | Required transport, binding, dispatch, and diagnostic effects |
| Change one HTTP mapping with a shared binding shape | Mapping output changes without regenerating an identical binding implementation |
| Add/remove JSON root; change nested description/range/constructor metadata | All dependent metadata changes; unrelated roots stay stable where output partitioning permits |
| Add an irrelevant attribute; replace a feature DLL without a contract change | No semantic output change; record reference discovery work |
| Replace a feature DLL with an exported event/context change | Required consumers update; no stale cache result |
| Change analyzer options, global using, alias, path, or parse options | A separate expected diagnostic/output manifest for each semantic change |
| Incomplete edit, undo, redo | No generator exception; final restored input matches a clean generation |

Expected changes are defined per output family and semantic dependency, with known partition
fan-out listed explicitly. Track source-content hashes, hint names, diagnostics and their locations,
and incremental step reasons separately. `Unchanged` means a transform ran and compared equal;
`Cached` means reuse. Neither unchanged bytes nor a broad count of cached steps proves cheap work.
Roslyn may rerun semantic transforms in unchanged files after another file changes. Require reuse
at the appropriate downstream model/emission boundary rather than forbidding all rebinding.
[Roslyn's incremental generator cookbook explains this equality boundary.](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md#pipeline-model-design)

## Analyzer operations

Measure each analyzer alone with concurrency disabled for attribution. Measure the five-analyzer
suite with normal concurrent execution for qualification. Do not sum isolated analyzer timings:
concurrency and shared semantic work change the total.

Run full compilation analysis using `CompilationWithAnalyzers.GetAnalyzerDiagnosticsAsync` on a
fresh driver, and report compilation-end diagnostics explicitly. Separately request syntax and
semantic diagnostics for the changed document or span. These public APIs are controlled probes;
they do not reproduce an IDE scheduler's debounce, background work, or cross-project orchestration.
Their diagnostic set may omit compilation-end results, so verify it against the appropriate subset.

For each edit measure both (a) analysis on a derived compilation with unchanged trees reused and
(b) the complete request: incremental parsing, generators, then the requested analysis. Distinguish
the first request from repeated requests against the same analyzer driver, which can return cached
diagnostics. Label compiler binding costs as included or prewarmed; do not compare unlike modes.
Verify fixtures in a separate world so `GetDiagnostics` does not silently prebind the timed input.

Include a small replay in the primary contributor IDE alongside the first Standard baseline,
before freezing interactive budgets. Record host/version/settings, enabled extensions, background
analysis scope, completion signals, and edit-to-diagnostics latency. Associate requests/results with
document versions so stale diagnostics cannot count as fast completion. Replay an unrelated body
edit, an incomplete request followed by repair, and a four-project contract change; observe generated
documents and diagnostic refresh. Record first feedback and complete requested diagnostics separately.
If the host cannot expose a reliable completion boundary, record that limitation and qualify only
the public API probes until an adapter can measure it. Later expand the replay for broader editor
claims; background scheduling and analysis scope can change the result substantially.

## Code fix stages and actions

Benchmark action discovery, preview computation, change computation without preview, workspace
application, and the complete user sequence. Use fresh actions and starting solutions for the
isolated stages; also measure preview followed by apply on the same action to capture real reuse.

| Stage | Boundary |
|---|---|
| Discover | `RegisterCodeFixesAsync` until the complete expected action list is registered |
| Preview | `GetPreviewOperationsAsync` with all operations materialized; rich IDE diff rendering is measured only in a host run |
| Compute changes | `GetOperationsAsync` without a preceding preview, fully awaited |
| Apply prepared changes | Apply the prepared operations once to a workspace starting at the original solution |
| Complete action | Discover, optional preview, compute, apply, and request fresh diagnostics; report child stages and total |
| Fix All | `FixAllProvider.GetFixAsync`, operation computation, application, and diagnostic refresh; report enumeration and diagnostic-fetch counts |

Run code-fix-only cases with verified recorded diagnostics so analyzer cost is attributed elsewhere.
Also run the complete request with real analyzer/generator diagnostics. Record the workspace kind
and capabilities. AdhocWorkspace application measures an in-memory transform; filesystem persistence
and UI rendering need their own host measurement.

| Provider | Action and scale cases |
|---|---|
| `ApplicationStructureCodeFixProvider` | Split type (`PORTIA200`), rename file (`201`), synchronize namespace/references (`202`), make version explicit (`210`); name collisions, partial types, directives, linked files, nested constructor references, unavailable workspace capabilities |
| `JsonMetadataCodeFixProvider` | Create context, add root to one/multiple/partial contexts, already-rooted/generated contexts, no candidate context; first discovery, same-project cache hit, new project after unrelated/context edits, concurrent discovery |
| `PartialProcessorCodeFixProvider` | Make processor/enclosing types partial (`PORTIA002`, `005`, `015`); nested depth, shared enclosing types, nonrepairable shape diagnostics |

For namespace changes vary 1/100/1,000/10,000 references across 1/5/20 projects. For JSON discovery
vary unrelated documents independently of context count. For Fix All vary 1/10/100/1,000 diagnostics,
including many in one document and one per document. Cover document/project/solution scopes that
the provider supports. JSON supports document/project only; structure currently supports Fix All
for `200`/`201`/`202`, not `210`. Assert unavailable scopes/actions are absent rather than counting
a fast no-op as success. Partial processing uses Roslyn's BatchFixer; include supported host scopes
and collisions from several diagnostics targeting the same enclosing type.

Verify expected modified/added/removed documents, text, paths, names, references, diagnostic removal,
and consumer compilation after every applied action. Recompute diagnostics and action discovery on
the changed solution: a repaired violation should no longer offer its repair. Any supported replay
must have its documented idempotent result. Namespace correctness includes dependent projects and
constructors.

## Allocation and retention

Use `GC.GetTotalAllocatedBytes(precise: true)` deltas for a fully awaited operation in an otherwise
idle worker, plus process CPU time, GC counts, and wall time. Read counters outside the wall timer.
Run process allocation measurement separately when its counter synchronization perturbs short cases.
Record an empty synchronous/async operation as an overhead control; do not automatically subtract
it from results. Batches are allowed only for genuinely repeatable operations with equivalent state.

Per-thread allocation counters cannot be the authoritative measure for asynchronous code fixes
or concurrent analyzer callbacks. Process allocation includes Roslyn and worker activity, so keep
the worker idle, record the boundary, and use traces for attribution. BenchmarkDotNet documents
its MemoryDiagnoser's thread-local counter and separate diagnostic runs in its
[diagnoser guidance](https://benchmarkdotnet.org/articles/configs/diagnosers.html).

Allocated bytes, live managed bytes, and process resident memory answer different questions.
For retention, run 1,000 edits in a bounded session, keeping only the current driver/solution and
fixed fixture bytes. Sample after 100/500/1,000 edits, then release the session. Use separate forced-GC
checkpoints and weak references to old projects/compilations to detect retained roots; never force
GC inside latency measurements. Compare against an equivalent Roslyn-only session, take a heap
trace if growth persists, and account for intentional current-state/static metadata caches.

## Correctness and responsiveness gates

Before timing, independently verify each scenario's activation, expected diagnostics/action, generated
output, and compilation. Verify equivalent outputs outside every independent measured repetition as well;
normalize diagnostics by ID, severity, arguments and location, and generated sources by generator
and hint name. Inputs with intentional compile errors have an explicit expected-error manifest.
Timing cases must not silently skip an operation after discovering zero actions or diagnostics.
For cumulative sessions, do full intermediate compilation/hashing in a separate verification replay;
interleaving them with timed edits would warm semantic caches and alter the session. Check the final
measured session state after its last timer and buffer metrics without retaining historical solutions.

Cancellation probes cover pre-cancellation where the API requires it, cancellation during substantial
discovery/generation/reference search, and a successful retry on the same supported state. Record
time from cancellation request to completion and the actual outcome. Distinguish operations that
finished before cancellation from in-flight cancellation. A canceled action publishes no partial
workspace mutation; a retry matches a clean run. Use bounded probes and a parent-process timeout.
Timeouts, crashes, analyzer exceptions, and missing outputs are failed cases with retained artifacts.

Keep deterministic output, invalidation and cancellation checks in ordinary CI. Follow repository
test conventions and red-to-green development for the harness: prove it catches a skipped generator,
missing action, stale output, overbroad invalidation and a broken state reset before trusting results.
Put wall-clock regression gates on the controlled performance worker rather than xUnit assertions.

## Comparisons and decision rules

Run baseline and candidate sequentially in balanced randomized pairs (AB/BA), using the same
scenario/input and separate processes. First run A/A pairs to measure harness and host noise.
Baseline compiler components must consume the same fixtures/contracts. When changed application
APIs prevent that, use a common supported fixture for comparison and report candidate-only feature
cases separately; never quietly remove the new path to improve the aggregate.

A tuning run starts with 5 process pairs and 10 operations per warm scenario. Qualification uses
10 pairs and 20 operations per warm scenario. Each operation resets the required state; the edit
session is a separate sequence. Run first-request cold cases once per process and report the median
and interval; collect at least 100 independent launches before making a cold P95 claim. For warm
P95, collect at least 100 operations across at least 10 processes, and retain request order.

Report median/P95, allocations, total CPU, spread and the paired candidate/baseline ratios per
scenario. Resample process pairs as clusters for a 95% confidence interval; operations within a
worker/session are correlated. Never pool all workloads into one average or infer a change by
checking whether separate timing intervals overlap. A tuning pilot selects targets; confirm a claimed
gain in a fresh comparison rather than continuing to sample until a favorable interval appears.

Start with these proposed regression limits, frozen before the comparison:

| Measure | Allowed increase relative to baseline | Absolute allowance |
|---|---:|---:|
| Warm latency, including P95 | 5% | 1 ms per operation |
| Cold/full-build latency | 5% | 10 ms per operation |
| Operation allocations | 5% | 64 KiB per operation |
| Generated bytes/files | Scenario's declared output budget | Intentional feature changes require a reviewed budget change |

For latency/allocation, the permitted increase is the larger of the percentage and absolute
allowance. Pass when the paired interval's upper bound is inside that margin; fail when its lower
bound exceeds it; otherwise report inconclusive. A/A noise exceeding the permitted margin makes
the timing gate unusable until the host or measurement is improved. Multiple scenarios are gates
individually; mark isolated marginal findings provisional until an independent repeat confirms them.
Use paired absolute deltas when the baseline is zero or near timer resolution; do not divide by
zero or report an enormous percentage for negligible work.

Regression limits protect the current result. Absolute responsiveness goals identify tuning work
even when the baseline is equally slow. Initial goals on the designated reference host are:

| Standard fixture operation | Initial goal |
|---|---:|
| Complete unrelated-body edit request, generators plus changed-document analysis | P95 <= 100 ms |
| Fix discovery | P95 <= 50 ms on a cached snapshot; <= 150 ms on first discovery |
| Local fix preview/change computation | P95 <= 100 ms |
| Namespace fix, 1,000 references across 5 projects | P95 <= 1 second |
| Fix All, 100 diagnostics in the Standard solution | P95 <= 3 seconds |
| In-flight cancellation response on a substantial operation | P95 <= 100 ms after cancellation |

These are design targets, not measured promises. Establish absolute allocation, first-open and
full-build budgets in the initial baseline report before tuning: their values depend on the pinned
fixture and host. Freeze those budgets for subsequent revisions. Record every missed goal as work;
an unchanged slow baseline does not satisfy responsiveness qualification. For scale sweeps, flag
growth faster than the modeled input/output work and investigate the trace rather than declaring
all operations must scale linearly. Reference search and reachable contract graphs have distinct axes.

## Host controls and artifacts

Qualification starts on one dedicated macOS Arm64 machine, then repeats the combined cases on
Linux x64 for CLI/package behavior and Windows for any claimed Visual Studio behavior. Keep host
baselines separate. Restore and build outside timed operations, pin power mode and architecture,
prevent sleep, and run no parallel performance workers. Record OS/CPU/RAM, SDK/runtime/Roslyn and
dependency hashes, GC/JIT settings, power/thermal state where available, and background CPU pressure.

Predeclare host acceptance from A/A calibration. Start with a 10-second idle window below 10%
total CPU use before a pair, normal memory pressure, stable power mode, and no reported thermal
throttling. Record platform support for these signals; missing telemetry is explicit. During the pair,
sample pressure and CPU use outside the worker process tree rather than rejecting the worker's own
legitimate CPU activity. Freeze the background-activity threshold after A/A calibration. Mark a
pair invalid when that policy fails; retain it with the reason and do not erase statistical outliers
because they look inconvenient. Bound tuning runs to 5 minutes and selected qualification batches
to 30 minutes by default; if exhausted, retain completed samples and report incomplete. Bigger
scale/cold-tail runs require an explicit longer run profile. Stop a campaign when host conditions
repeatedly fail, preserving the issue's existing pause on uncontrolled runtime campaigns.

Each result records revision and dirty diff hash, harness/fixture/manifest versions, component and
scenario, actual assembly hashes, process/pair/operation index, seed, dimensions, state/cache mode,
timestamps, latency/CPU/allocation/GC data, verification status, output hash, and host-validity status.
Retain raw JSONL, summary with comparison intervals, generated-source manifests, verification logs,
and optional profiles in an artifact directory keyed by revision and content hash. Give reports an
immutable download location and checksum. Store compact accepted budgets and the artifact index
in the repository; keep large traces/raw source captures in retained build or release artifacts.

## Implementation order

1. Build the fixture manifest and verification mode for all eight generators, five analyzers, and
   three code-fix providers. Include negative controls and the newer JSON metadata paths.
2. Build one correct worker/controller boundary. Prove independent edit resets, process allocation
   accounting across async callbacks, A/A noise behavior, and rejection of skipped work.
3. Add Small/Standard combined generator/analyzer operations and every code-fix action/stage.
   Produce the initial baseline with the primary IDE pilot, then freeze budgets. This establishes
   where tuning is needed.
4. Profile the largest measured costs. Prioritize frequent interactive operations, then first-open
   and large-scale costs. Confirm each improvement with focused and combined comparisons.
5. Add scale sweeps, edit-session retention, and packed Accounts consumer builds with SDK analyzer
   reporting/binlogs. Include no-change incremental MSBuild and forced compilation separately;
   a skipped compiler cannot demonstrate cheap analyzer/generator execution.
6. Add supported IDE replay and platform runs for broader claims. Ordinary PR CI verifies behavior;
   a manual or scheduled controlled worker runs timing gates and publishes the evidence.

The issue's tooling acceptance requires complete coverage, reproducible comparisons, reviewed
budgets, resolved regressions, correct outputs, bounded cancellation and retention, and a qualified
consumer build. Claims identify the exact host and fixture coverage. Jev reviews the measurement
contract and candidate explanations; source, correctness checks and recorded runs decide acceptance.

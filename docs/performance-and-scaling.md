# Performance and scaling

These measurements describe this checkout, not a promise about another application or machine.
They were recorded on 2026-09-10 and refreshed on 2026-09-12 on an Apple M5 (10 physical
cores), macOS Tahoe 26.6.2 (25G83), .NET SDK 10.0.400, and .NET 10.0.11 Arm64 RyuJIT.
BenchmarkDotNet used its `ShortRun` job except for the deliberately bounded large-history run.

Reproduce the measurements from the repository root:

```sh
dotnet build Portia.slnx --configuration Release
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --filter 'Cntryl.Portia.RequestDispatchBenchmarks.*' --job Short
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --filter 'Cntryl.Portia.AggregateRepositoryBenchmarks.Hydrate' --job Short
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --filter 'Cntryl.Portia.LargeAggregateRepositoryBenchmarks.Hydrate'
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --filter 'Cntryl.Portia.ProcessorDispatchBenchmarks.*' --job Short
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --filter 'Cntryl.Portia.HttpBindingBenchmarks.*' --job Short
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --filter 'Cntryl.Portia.DomainEventSerializationBenchmarks.*' --job Short
dotnet run --configuration Release --no-build --project bench/Portia.Benchmarks -- \
  --filter 'Cntryl.Portia.ReactorDispatchBenchmarks.*' --job Short
```

Short runs are useful for guarding a focused change but have wider confidence intervals than a
full benchmark campaign. Compare before and after in the same session and do not turn these values
into xUnit timing assertions.

## Measured hot paths

Request dispatch now reuses one typed request context and a cached pipeline plan while enforcing
single-use continuations. The same-session comparison was:

| Pipeline | Before | After | Allocation before | Allocation after |
|---|---:|---:|---:|---:|
| No behaviors | 113.10 ns | 90.10 ns | 48 B | 0 B |
| One behavior | 157.29 ns | 128.94 ns | 224 B | 216 B |
| Five behaviors | 416.08 ns | 310.86 ns | 992 B | 216 B |
| One asynchronously yielding behavior | - | 1.95 us | - | 984 B |

The one- and five-behavior means fell 18% and 25%, respectively. Five-behavior allocation fell
78%, and the no-behavior path became allocation-free. The yielding case makes the asynchronous
path visible instead of allowing a synchronous-only optimization to look complete.

Aggregate hydration retains a complete validation pass before applying anything. The aggregate
pre-sizes its permanent event-ID set from that validated batch rather than repeatedly growing it:

| History | Before | After | Allocation before | Allocation after |
|---|---:|---:|---:|---:|
| 32 events | 2.403 us | 2.401 us | 3,336 B | 2,424 B |
| 512 events | 37.145 us | 33.225 us | 57,128 B | 23,752 B |
| 10,000 events | - | 2.417 ms | - | 1.40 MB |
| 100,000 events | - | 22.978 ms | - | 12.06 MB |

The large-history measurements predate the event-ID capacity change and are retained to show the
linear replay shape, not as updated allocation figures. That job uses one invocation per iteration
so pilot selection cannot create multi-gigabyte temporary allocation; its values are measured, not
extrapolated.

Processor dispatch has an important semantic tradeoff:

| Processor, 512 events | Before | After | Allocation before | Allocation after | Commit boundary |
|---|---:|---:|---:|---:|---|
| `Projector` | 90.46 us | 70.19 us | 102,600 B | 37,192 B | Once per event |
| `BatchProjector` | 12.60 us | 12.65 us | 12,864 B | 12,864 B | Once per bounded batch |

Use the single-event base when each event genuinely needs an independent atomic
data-and-checkpoint commit. Prefer a batch base for high-volume processing when the repository can
atomically commit the bounded batch. A batch reduces framework and persistence overhead but does
not make external effects transactional.

Projector identity and immutable handler context are now created once per pass. This removes
per-commit reconstruction without changing the storage transaction or checkpoint boundary.

Reactor execution had a separate allocation source: every triggering event copied and revalidated
the same system principal. The runner now validates one private snapshot per pass, while every
public `context.Actor` access still returns an isolated copy:

| Reactor, 512 events | Before | After | Allocation before | Allocation after |
|---|---:|---:|---:|---:|
| `Reactor` | 423.38 us | 306.52 us | 420.82 KB | 69.34 KB |
| `BatchReactor` | 392.75 us | 230.01 us | 352.95 KB | 53.36 KB |

The batch timing comparison is directional because its three-iteration ShortRun had a wide spread;
the allocation reduction was deterministic. Real reactor effects normally dominate these framework
figures.

Domain-event envelopes avoid intermediate JSON object trees on the exact-version path. Upcasting
still materializes a mutable `JsonObject` only when an old payload actually needs transformation:

| Operation | Payload | Before | After | Allocation before | Allocation after |
|---|---:|---:|---:|---:|---:|
| Serialize | 16 B | 1.792 us | 0.731 us | 1,584 B | 1,224 B |
| Serialize | 256 B | 1.979 us | 0.764 us | 2,064 B | 1,704 B |
| Serialize | 4 KiB | 3.563 us | 1.729 us | 9,744 B | 9,384 B |
| Deserialize | 16 B | 3.301 us | 1.802 us | 2,240 B | 840 B |
| Deserialize | 256 B | 3.146 us | 1.964 us | 2,960 B | 1,320 B |
| Deserialize | 4 KiB | 5.927 us | 3.108 us | 14,480 B | 9,000 B |

HTTP benchmarks parse `JsonDocument` during setup, so the warm cases isolate generated member
binding and metadata lookup:

| Binding shape | Mean | Allocation |
|---|---:|---:|
| Default metadata | 58.09 ns | 0 B |
| Property converter | 63.20 ns | 32 B |
| Property number handling | 55.52 ns | 0 B |
| Mixed 20-member request | 1.838 us | 0 B |
| Cold property-converter metadata with fresh options | 5.294 us | 9,217 B |

The converter's 32 B is its string value, not an options clone. The cold diagnostic uses a new
application-options identity for every invocation; the warm converter path is about 84 times
faster and no longer allocates a derived `JsonSerializerOptions` per member. Cache entries are
weakly keyed by the application's options instance, so one application configuration neither
roots nor contaminates another.

## Runtime scaling model

A per-tenant registration creates one logical workload for every active tenant. With `N` tenants
and `M` per-tenant processors, the coordinator therefore sees `N x M` independently identified
workloads; Portia does not coalesce or serialize those identities. Every processor pass still gets
a fresh dependency-injection scope. Its event subscription is retained across successful passes,
then disposed and recreated after a notification failure. Polling remains the authoritative
reconciliation backstop.

Each queue runner reserves and dispatches one delivery at a time, acknowledging successful work
and leaving failures unacknowledged for the transport. Scale queue throughput with additional
processes and independently routed consumers. Portia does not expose in-process prefetch, delivery
parallelism, or a hidden workload scheduler.

`IResumableTenantDirectory` is an optional capability. `MultiTenantRunner` opens one cursor and
reuses it across watch failures, so an `EventSourcedTenantDirectory` resumes at its next in-process
offset and observes removals that occurred while disconnected. Separate cursors progress
independently. Cursor progress is not persisted across process restarts. Legacy
`ITenantDirectory` implementations continue to use a complete active-membership enumeration plus
live watch.

`EventSourcedTenantDirectory` reports each bounded read pass through
`portia.tenant_directory.operation.duration`, `portia.tenant_directory.event.count`, and
`portia.tenant_directory.active.count`. The bounded `portia.operation` tag identifies a snapshot,
legacy watch, or resumable cursor; `portia.phase` distinguishes the initial `replay` from later
`catch_up` passes; and `portia.outcome` distinguishes success, cancellation, interruption, and a
reader fault. Duration includes only time spent advancing the durable reader; downstream lifecycle
consumer and workload backpressure is excluded. Tenant identities, stream addresses, and cursor
values are deliberately excluded.

Use these measurements with `portia.workload.active` and `portia.processor.lag` when startup is
slow. A long successful replay whose event count grows with lifecycle history indicates replay
cost; compare its active-tenant count with workload-active growth to expose per-tenant fan-out. A
faulting pass that repeatedly stops at the same event count points to an unreadable or poison
lifecycle event. Successful catch-up passes while processor lag remains flat or rises instead point
to processor checkpoint progress, not tenant discovery. No duration by itself proves a stalled
checkpoint: confirm that event counts and processor lag fail to advance across repeated samples.
For large tenant lifecycle histories, construct `EventSourcedTenantDirectory` through
`TenantDirectorySnapshots.Create<TStartEvent,TStopEvent>` and pass a
`FitzKvTenantDirectorySnapshotStore` backed by a dedicated `kv://` base route. The store commits
the active roster and lifecycle cursor as one value after the initial complete read pass and then
after 256 further events. On restart the directory resumes from that cursor and catches up before it announces active tenants. Concurrent
workers save conditionally, so a stale replay cannot replace a newer snapshot. Without a snapshot
store, the directory continues to replay from the beginning.

KV projections, reactor checkpoints and tenant snapshots use buffered commits by default; event
stream commits remain synchronous. Buffered KV batches retain atomicity but may need more replay
after a crash. Each KV store exposes an explicit durability constructor for applications choosing
synchronous persistence; reactor registration also accepts `UseKvCheckpoints(route, KvDurability.Sync)`.

Normal tenant removal has one `MultiTenantRunnerOptions.TenantStopTimeout` deadline shared by
workload cancellation and the stop callback. Ignoring that deadline faults the runner with
`TenantStopTimeoutException`; host shutdown continues to use the shared `ShutdownGrace` budget.

## Persistence and delivery boundaries

Aggregate snapshots and prefix truncation are unsupported. Archiving old events is safe only when
every aggregate reader still presents the original contiguous physical offsets; renumbering a
suffix or hiding its prefix violates hydration's continuity contract. Where the domain permits,
prefer an explicit rollover to a new aggregate identity and stream.

An atomic event-store append covers one aggregate stream. Portia does not provide multi-stream
transactions, a transactional request outbox, or cross-aggregate rollback. Model cross-aggregate
workflows with events and reactors, and make every at-least-once reaction safe to replay by using a
stable effect ID or an integration-owned inbox/outbox with the target's transaction.

Terminal queue outcomes include permanent handler failure, actor-validation failure, and reaching
an explicitly configured retry limit when the transport reports a durable attempt count. Fitz's
wire protocol does not report queue attempts, so its worker rejects a positive
`QueueRunnerOptions.TerminalAttempt` at startup. An application-selected `IQueuedRequestTerminalHandler` is optional;
it observes terminal failures but cannot acknowledge them. Portia abandons terminal deliveries and leaves
expiration, redelivery, and dead-letter policy to the transport. Direct and generic hosted runners validate
a disposable scope before enumerating the transport, and each delivery scope is rechecked. Callback effects
must tolerate replay. The Fitz adapter retains delivery ownership when terminal callback execution faults;
Portia does not claim a broker-independent durable dead-letter transaction.

HTTP input is buffered in full and bounded to 10 MiB by default, including unknown-length bodies
declared for a custom `OnBind` binder. Oversize bodies return 413 before binding or dispatch. The
default binder accepts object JSON or, for scalar-only bodies, an HTML form; binary bodies need a
custom binder and are replayed from the start after bounded buffering.

## Capability matrix

| Capability | Bundled production implementation | Testing support | Application responsibility |
|---|---|---|---|
| `IEventStore` | `FitzEventStore` | `InMemoryEventStore`, `EventStoreConformance` | Implement and run conformance for another backend |
| `IProjectionStore` | `FitzKvProjectionStore` is an abstract Fitz-KV base | `ProjectionStoreConformance` | Supply the projection data operations and atomic checkpoint commit |
| `IProjectionCheckpointStore` | `FitzKvCheckpointStore` | `InMemoryProjectionCheckpointStore` | Supply durable reactor progress without Fitz |
| `IWorkloadCoordinator` | Fitz fleet coordination; `SingleProcessWorkloadCoordinator` for one replica only | `WorkloadCoordinatorConformance` | Run distributed conformance before scaling a custom coordinator beyond one replica |
| `IPermissionEvaluator` | None | `TestPermissionEvaluator` | Register the application's permission policy when any permission-protected request is selected |
| `ITenantDirectory` | `EventSourcedTenantDirectory<TStartEvent,TStopEvent>`; also supports resumable cursors | In-process fakes in the test suites | Define authoritative membership events, mapping, and registration |

Hosted startup rejects permission-protected request registrations when `IPermissionEvaluator` is
unavailable and reports those CLR types in stable order. This belongs at the composed-host boundary:
handlers and registrations can arrive from multiple feature assemblies, so no analyzer examining
one compilation can prove that the final service graph supplies an evaluator. Direct, non-hosted
composition remains available for focused tests.

`PORTIA100` and `PORTIA101` are best-effort architecture warnings, not proofs of purity or
dependency transparency. `PORTIA100` recognizes Portia dispatch/effect APIs, `HttpClient` and
`IHttpClientFactory`, `SmtpClient`, Stripe clients, EF Core contexts, ADO.NET connections, and
generated gRPC client ancestry, except a dependency that implements `IProjectionStore` or
`IProjectionCheckpointStore`, such as an EF Core repository the projector commits through. `PORTIA101` recognizes DI service-provider/scope dependencies and
semantic `ActivatorUtilities` calls. An application-defined gateway without one of those known
markers remains intentionally unreported and still needs architectural review. `PORTIA105` and
`PORTIA106` report only Portia's own write and dispatch APIs in request guards and authorizers:
`IRequestBus` and the other senders, the scheduler, `IAggregateWriter`, `IAggregateExecutor`, event stores,
and `IProjectionStore` or `IProjectionCheckpointStore` taken directly. Preflight often reads through an HTTP
policy service or a read-model database, so those clients, and application repositories that happen to
implement a projection contract, are not reported; `IAggregateReader` and `IDomainEventReader` remain allowed.

## Bounded processor passes

Projector and reactor workers enumerate at most `ProjectionRunOptions.MaxEventsPerPass` records per
dependency-injection scope (4,096 by default). A budget-exhausted backlog continues immediately in a
fresh scope only when the durable checkpoint advanced; a non-progressing pass falls back to the
normal notification or polling wait. Caught-up workloads retain notification and polling waits.
`MaxBatchSize` continues to control transactional batch size independently.

## Generated registration and dispatch

Shared event, JSON-context, and inferred-request registrations are emitted once per compilation.
Each generated block has a private reference identity; `PortiaBuilder.AddGeneratedRegistrations`
applies it once per builder. Another producing assembly or another service collection gets its
own initialization. Failed initialization removes the completion marker so a later call can retry.
Repeated completed blocks allocate no managed memory.

Request transport extension files contain at most 128 registration methods. Larger sets are
partitioned by a deterministic hash of the request identity, with further partitioning when a
bucket exceeds that bound. Source declaration order does not affect filenames or emitted text.
This output partitioning was added after the timing campaign below; its performance has not been
included in those results.

The previous registration emitter repeated the event catalog inside every interceptor. A
Platform.Core source probe with 2,874 files, 1,452 registration sites, and 874 events produced
1,278,252 interceptor lines and 206,939,136 bytes. The shared emitter produced 11,583 lines and
1,263,483 bytes with the same interceptor and event counts and no generator diagnostics. This
is generated-output evidence against that input snapshot, rather than a full Platform application
build or deployment qualification.

Generated typed batches use one exact-sized array for each contiguous handler group. The
contexts and batch storage remain owned by that delivery; they are not pooled or overwritten
after a handler returns. Single-event dispatch remains a direct typed switch. Ordinary aliases
for handlers or values no longer disable referenced-event name filtering across the compilation;
event aliases are resolved once, with conservative handling of namespace and unresolved aliases.

Use `bench/Portia.CompilerBenchmarks` for all generators, unchanged inputs, unrelated edits,
contract changes, and referenced-assembly changes. Use `ProcessorDispatchBenchmarks`,
`ReactorDispatchBenchmarks`, `HttpBindingBenchmarks`, and `RequestDispatchBenchmarks` for the
runtime paths, alongside the reflection-disabled runtime qualification matrix. Source size and
allocation counts are separate from timing distributions; a short local benchmark is not a
release performance guarantee.

The 2026-10-30 compiler qualification compared the clean source at `7d7e3c4` with the current
working-tree implementation on .NET 10.0.14, Arm64, and Roslyn 5.9.0. It used the same source,
generated-output, referenced-feature, and analyzer diagnostic controls in both runs. The combined
workload had 1,000 commands, 10 feature assemblies, 10 JSON contexts, 100 HTTP mappings, and
10,000 unrelated types. Twenty compiler samples covered every edit below; five MSBuild samples
covered cold and unchanged builds. All eight Portia generators emitted their expected outputs,
all five analyzers produced their targeted diagnostics, and the generated consumer compiled.

| Combined compiler case | P50 before | P50 after | Driver allocation before | Driver allocation after |
| --- | ---: | ---: | ---: | ---: |
| Cold driver | 648.3 ms | 295.0 ms | 236.1 MB | 238.5 MB |
| Unchanged compilation | 122.7 ms | 61.8 ms | 56.5 MB | 59.2 MB |
| Unrelated body edit | 625.1 ms | 335.8 ms | 133.5 MB | 130.4 MB |
| Call-site whitespace | 454.2 ms | 352.3 ms | 132.8 MB | 129.7 MB |
| Event add | 568.4 ms | 373.9 ms | 234.2 MB | 235.6 MB |
| Event remove | 453.4 ms | 358.5 ms | 144.0 MB | 146.3 MB |
| Event rename | 413.7 ms | 203.6 ms | 141.6 MB | 144.1 MB |
| Route change | 420.3 ms | 180.0 ms | 132.4 MB | 129.3 MB |
| Referenced feature replacement | 426.0 ms | 325.9 ms | 123.2 MB | 120.1 MB |

The sum of the eight isolated Portia generator P50s fell from 845.1 ms to 418.8 ms for a cold
driver and from 130.2 ms to 70.3 ms for an unchanged compilation. Their isolated managed
allocations were 203.5 MB before and 205.6 MB after for a cold driver, and 58.2 MB before and
60.9 MB after for an unchanged compilation. Latency improved substantially; the 30% allocation
reduction target was not met.

System.Text.Json is reported separately. Its isolated cold P50 fell from 300.2 ms to 133.4 ms,
while allocations stayed effectively flat at 149.2 MB. Its unchanged P50 was 5.58 ms before and
4.35 ms after, with 1.07 MB allocated in both runs.

| Analyzer | P50 before | P50 after | Allocation change |
| --- | ---: | ---: | ---: |
| Application boundary | 3,502.9 ms | 2,092.0 ms | +1.1% |
| Application contract | 3,027.4 ms | 1,881.7 ms | +1.5% |
| Application structure | 2,884.1 ms | 1,868.7 ms | +1.8% |
| Component practice | 3,710.8 ms | 2,143.8 ms | +1.2% |
| Scenario observation | 3,137.2 ms | 2,483.2 ms | +1.2% |

The combined five-sample cold build P50 changed from 11.83 s to 11.65 s, and the unchanged build
P50 changed from 524 ms to 480 ms. The source hash, generated hash, and 14,979,016 generated bytes
matched between compiler runs. The final syntax-only data-shape refinement in `PORTIA204` and
filesystem collision preflight in the code fix landed after these timing samples, so those small
changes are not included in the measurements. Raw samples and environment records remain outside
the repository at `/tmp/portia-analyzer-followup`.

The namespace code-action campaign used one source project, one consuming project, and 1,000
qualified constructor references. It measured discovery, preview, and application separately;
application used `AdhocWorkspace.TryApplyChanges`, so it does not qualify disk-backed or
`MSBuildWorkspace` file operations. P50 and P95 use nearest-rank percentiles. The clean baseline
has 20 samples and the current implementation has 40 samples across two runs:

| Namespace code-action phase | P50 before | P50 after | P95 before | P95 after |
| --- | ---: | ---: | ---: | ---: |
| Discovery | 737.6 ms | 0.71 ms | 798.6 ms | 3.74 ms |
| Preview | 721.9 ms | 746.6 ms | 748.1 ms | 919.4 ms |
| Apply changes | 20.15 ms | 0.063 ms | 31.62 ms | 0.12 ms |
| All three phases, summed per sample | 1,483.6 ms | 747.2 ms | 1,546.2 ms | 921.6 ms |

Discovery P50 fell by 99.9%, and total measured P50 fell by 49.6%. Preview P50 rose 3.4%, while
its P95 rose 22.9%; that tail regression repeated in both current runs and remains an optimization
target. Deferring cross-project reference rewriting removes it from code-action discovery, but the
preview still performs that work. Application timings cover in-memory solution application only.
The benchmark source and raw samples remain outside the repository at
`/tmp/portia-analyzer-followup/{baseline,current}-namespace-action` and
`/tmp/portia-analyzer-followup/current-namespace-action-repeat`.

The local `ProcessorDispatchBenchmarks` ShortRun measured the following allocation changes for
generated typed batches. The single-event controls retained their original allocation counts.
Timing distributions were noisy, so no dispatch latency improvement is claimed from this run.

| Events per typed batch | Growing list | Exact-sized array |
| --- | ---: | ---: |
| 1 | 504 bytes | 448 bytes |
| 32 | 2,512 bytes | 2,184 bytes |
| 512 | 33,328 bytes | 29,064 bytes |

## Shape emission follow-up

The local follow-up shares HTTP binder/dispatch implementations by structural binding shape,
keeps interception wrappers in files grouped by calling type, and bounds implementation groups
to 128 shapes. Mapping invocations still own their endpoint configuration and handler closures.
Binding-shape equality excludes diagnostic and interception locations, so moving a call site does
not regenerate its implementation. Processor implementation models likewise exclude diagnostic
locations; dispatch order and delivery-owned batch arrays are unchanged.

Shared registration and event-catalog statements use deterministic bounded helper trees. One
completion marker still surrounds the whole registration block, including its existing failure
recovery. Identical registration wrappers share an implementation within their calling type.
Referenced JSON coverage and code-fix context discovery use weakly owned immutable snapshots.
The HTTP and registration emitters write their layout directly: an initial parse-and-normalize
pass regressed compiler time and allocations and was removed before the final measurements.

Twenty samples per workload compared the clean `7d7e3c4` generators with the local candidate on
identical inputs. Both workloads had 100 mappings: one repeated ten request shapes, while the
other used 100 distinct shapes. The source hashes matched within each comparison, and the
generated consumer plus all edit variants compiled.

| Isolated HTTP generator, cold | P50 before | P50 after | Allocation before | Allocation after |
| --- | ---: | ---: | ---: | ---: |
| 100 mappings, 10 shapes | 7.36 ms | 8.46 ms | 6.03 MB | 2.95 MB |
| 100 mappings, 100 shapes | 11.08 ms | 10.79 ms | 8.00 MB | 8.86 MB |

For repeated shapes, total generated output including the JSON generator fell from 971,507 to
267,372 bytes (72.5%). Isolated HTTP allocation fell 51.1%, while isolated cold P50 rose 14.9%.
The complete driver, including System.Text.Json, changed from 26.86 to 22.20 ms cold P50 and
10.09 to 6.38 MB allocated. Distinct shapes had essentially flat cold latency and 10.7% higher
HTTP allocation. This is a source-size and repeated-shape allocation improvement, not a universal
compiler latency win; the 30% latency target remains unmet in these workloads.

Raw samples are outside the repository under `/tmp/portia-optimization-final`, in
`baseline-repeated-20`, `baseline-distinct-20`, `final-repeated-20`, and `final-distinct-20`.
The local runtime qualification matrix and macOS Arm64 NativeAOT HTTP consumer both passed.
Those are behavior checks; no runtime throughput improvement is claimed from them.

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

Each queue runner reserves, dispatches, and acknowledges one delivery at a time. Scale queue
throughput with additional processes and independently routed consumers. Portia does not expose
in-process prefetch, delivery parallelism, or a hidden workload scheduler.

`IResumableTenantDirectory` is an optional capability. `MultiTenantRunner` opens one cursor and
reuses it across watch failures, so an `EventSourcedTenantDirectory` resumes at its next in-process
offset and observes removals that occurred while disconnected. Separate cursors progress
independently. Cursor progress is not persisted across process restarts. Legacy
`ITenantDirectory` implementations continue to use a complete active-membership enumeration plus
live watch.

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
an explicitly configured retry limit when the transport reports a durable attempt count. Fitz 1.0
does not report queue attempts, so its worker rejects a positive `QueueRunnerOptions.TerminalAttempt`
at startup. Each supported terminal outcome requires `IQueuedRequestTerminalHandler`; missing one
faults the runner before acknowledgment or abandonment. The callback completes before the single
transport acknowledgment. Those operations are not atomic, so the handler must tolerate replay.
The Fitz adapter retains delivery ownership when either terminal callback setup or execution
faults; Portia does not claim a broker-independent durable dead-letter transaction.

HTTP input remains an object JSON body buffered in full and bounded to 10 MiB by default. Oversize
bodies return 413. Multipart, form, binary, and streaming request bodies are unsupported transport
shapes.

## Capability matrix

| Capability | Bundled production implementation | Testing support | Application responsibility |
|---|---|---|---|
| `IEventStore` | `FitzEventStore` | `InMemoryEventStore`, `EventStoreConformance` | Implement and run conformance for another backend |
| `IProjectionStore` | `FitzKvProjectionStore` is an abstract Fitz-KV base | `ProjectionStoreConformance` | Supply the projection data operations and atomic checkpoint commit |
| `IProjectionCheckpointStore` | `FitzKvCheckpointStore` | `InMemoryProjectionCheckpointStore` | Supply durable reactor progress without Fitz |
| `IWorkloadCoordinator` | Fitz fleet coordination; `SingleProcessWorkloadCoordinator` for one replica only | `WorkloadCoordinatorConformance` | Run distributed conformance before scaling a custom coordinator beyond one replica |
| `IPermissionEvaluator` | None | `TestPermissionEvaluator` | Register the application's permission policy when any guarded request is selected |
| `ITenantDirectory` | `EventSourcedTenantDirectory<TStartEvent,TStopEvent>`; also supports resumable cursors | In-process fakes in the test suites | Define authoritative membership events, mapping, and registration |

Hosted startup rejects guarded request registrations when `IPermissionEvaluator` is unavailable
and reports the guarded CLR types in stable order. This belongs at the composed-host boundary:
handlers and registrations can arrive from multiple feature assemblies, so no analyzer examining
one compilation can prove that the final service graph supplies an evaluator. Direct, non-hosted
composition remains available for focused tests.

`PORTIA100` and `PORTIA101` are best-effort architecture warnings, not proofs of purity or
dependency transparency. `PORTIA100` recognizes Portia dispatch/effect APIs, `HttpClient` and
`IHttpClientFactory`, `SmtpClient`, Stripe clients, EF Core contexts, ADO.NET connections, and
generated gRPC client ancestry. `PORTIA101` recognizes DI service-provider/scope dependencies and
semantic `ActivatorUtilities` calls. An application-defined gateway without one of those known
markers remains intentionally unreported and still needs architectural review.
# Bounded processor passes

Projector and reactor workers enumerate at most `ProjectionRunOptions.MaxEventsPerPass` records per
dependency-injection scope (4,096 by default). A budget-exhausted backlog continues immediately in a
fresh scope only when the durable checkpoint advanced; a non-progressing pass falls back to the
normal notification or polling wait. Caught-up workloads retain notification and polling waits.
`MaxBatchSize` continues to control transactional batch size independently.

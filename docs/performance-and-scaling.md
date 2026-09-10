# Performance and scaling

These measurements describe this checkout, not a promise about another application or machine.
They were recorded on 2026-09-10 on an Apple M5 (10 physical cores), macOS Tahoe 26.6.2
(25G83), .NET SDK 10.0.400, and .NET 10.0.11 Arm64 RyuJIT. BenchmarkDotNet used its
`ShortRun` job except for the deliberately bounded large-history run.

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

Aggregate hydration retains a complete validation pass before applying anything. Passing the
repository's populated list directly and replaying its span removes the redundant array:

| History | Before | After | Allocation before | Allocation after |
|---|---:|---:|---:|---:|
| 512 events | 16.35 us | 16.72 us | 69,640 B | 65,520 B |
| 10,000 events | - | 2.417 ms | - | 1.40 MB |
| 100,000 events | - | 22.978 ms | - | 12.06 MB |

The 512-event allocation reduction is 4,120 B, consistent with removing a 512-element reference
array. Its mean changed by 2.3%. The large-history job uses one invocation per iteration so pilot
selection cannot create multi-gigabyte temporary allocation; its values are measured, not
extrapolated.

Processor dispatch has an important semantic tradeoff:

| Processor, 512 events | Mean | Allocation | Commit boundary |
|---|---:|---:|---|
| `Projector` | 45.27 us | 102,600 B | Once per event |
| `BatchProjector` | 5.55 us | 12,864 B | Once per bounded batch |

Use the single-event base when each event genuinely needs an independent atomic
data-and-checkpoint commit. Prefer a batch base for high-volume processing when the repository can
atomically commit the bounded batch. A batch reduces framework and persistence overhead but does
not make external effects transactional.

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
an explicitly configured retry limit. Each requires `IQueuedRequestTerminalHandler`; missing one
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
fresh scope from the durable checkpoint; caught-up workloads retain notification and polling waits.
`MaxBatchSize` continues to control transactional batch size independently.

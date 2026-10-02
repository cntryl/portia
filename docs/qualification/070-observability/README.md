# 0.7.0 observability qualification

Issues #125 and #124 retain the delivery span budget and application-owned exporters. Public APIs are unchanged. No MCP qualification claim is made; #137–#142 were deferred by the user.

## #125: actual exported runner logs

Queue, notice and trusted scheduled delivery process spans now remain current through terminal/disposition/fault logs and cleanup. Public RequestDispatch still owns its existing process span; runners use the internal dispatch path with their owned process. The fault logger preserves exception, event ID, template, level and structured fields. Process exception events are recorded once. Execute spans retain their handler/pipeline duration; process spans now include delivery disposition and scope cleanup.

The original exported-log regressions fail before the lifetime change (`runner-before.log`). Actual OpenTelemetry trace/log exporters verify successively delivered queue terminal outcomes, acknowledgment failures, handler faults, notice losses and faults, and trusted schedule outcomes. Ambient and concurrent controls require distinct linked roots, exact trace/span correlation, restored caller context and no additional spans. A terminal acknowledgment fault must retain one exception event and a fault process outcome. Existing linked-root tests and an actual parent-based root-sampling control protect received remote links, root independence and sampling policy.

Adversarial review found that CreateActivity with an empty parent could adopt the unrelated ambient activity at Start under the SDK exporter. Sampling now runs with an empty ambient context; an explicit non-W3C parent sentinel prevents Start from subsequently adopting the caller. The caller remains available for Activity.Stop restoration. This avoids inventing a sampled parent to force a root, and the sampling control requires an AlwaysOff root sampler to export no process. Read-failure terminal paths also start a linked root before logging. Duplicate exception recording was removed from delivery runner fault logs. Host cancellation during notification scope disposal records cancellation before rethrowing.

## #124: actual Prometheus query execution

`scripts/qualify-prometheus.sh OUTPUT` starts a fresh digest-pinned Prometheus 3.15.0 container, exports metrics through stable OTLP 1.19.1 with the full metrics-specific HTTP/protobuf endpoint and cumulative temporality, executes dashboard queries with bounded polling, retains JSON responses and removes only its own container. Readiness retries include startup empty responses.

The workload executes 321 actual generated RequestBus requests. Each of eight outcomes has 40 completions; an additional pending success measures request/workload gauges at one before release and zero afterward. Raw discovery, counts, histogram buckets and every query response are in `prometheus/`. Worker failure/restart and processor commit-age observations use explicitly controlled instrument fixtures, separate from real request dispatch. No timing-performance claim includes exporter waiting.

The documented backend names, underscore labels and suffixes come from exported series. Queries separate expected business refusals, cancellation and faults; histogram aggregation preserves `le`; active instruments are gauges. Processor lag measures event age at commit, not stalled backlog. Its interpolated p95 is approximately 9.55 seconds for the roughly five-second fixture because the explicit enclosing bucket is ten seconds. Idle rolling rates are not presented as zero.

A central stable Microsoft.CodeAnalysis.Analyzers 5.9.0 pin replaces the prerelease transitive analyzer brought by stable Roslyn. The CI dependency gate checks declared and locked external NuGet versions. Negative declared and transitive prerelease fixtures must fail. It does not claim to inspect arbitrary container/tool dependencies; Prometheus and the .NET SDK are independently recorded here. Generated packaged analyzers retain PrivateAssets isolation.

## Reproduction

```sh
dotnet restore Portia.slnx --locked-mode
python3 scripts/verify-stable-dependencies.py
dotnet format Portia.slnx --verify-no-changes --no-restore
dotnet build Portia.slnx -c Release --no-restore
dotnet test Portia.slnx -c Release --no-build --filter 'Category!=BrokerIntegration&Category!=StorageIntegration'
scripts/qualify-prometheus.sh /tmp/portia-prometheus-evidence
```

Final-head hosted qualification must additionally pass packed consumers, Linux NativeAOT, broker/storage tests and CodeQL; exact merged-commit checks gate tracker updates.

Focused runner, linked-root, sampling and existing telemetry controls: 131 passes, zero skips (`runner-controls.log`). CI runs the actual Prometheus qualification script on every final head and merged commit as well as the stable-dependency gate.

After rebasing onto `ff4fc50322a9874ca1ff5829982a4451f2554fb4`, the full Release suite passes 1,743 tests (1,160 core, 509 compiler/consumer, 71 existing MCP, three reflection-disabled), zero skips. The Release build has zero warnings/errors. Raw logs are retained. Runtime source is based on `4e64a73a1bdee4e08987ae3525242972c909ca50`; subsequent evidence/documentation changes do not change the measured workload.

Fresh isolated digest-pinned Fitz/S3 services also pass 61 broker/storage tests (24 core plus 37 consumer), zero skips (`broker-storage.log`). Project `portia070observability` uses ports 40901/49000/49001 and is removed with its own volume after qualification.

Additional adversarial assertions pass all 17 actual exported outcome cases: fault status/outcome, exactly one exception event, bounded runner/stage fields, request/transport fields and exception type remain in the exported structured state (`runner-structured-state.log`). The correlation mechanism reuses the existing process activity, with no logger scope or separate correlation-state allocation. Root creation is a correctness change, not an optimization or timing claim; no-listener paths still return before context work.

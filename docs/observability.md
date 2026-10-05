# Observability

Portia's core packages emit dependency-free diagnostics through `ActivitySource`, `Meter`, and
source-generated `ILogger` calls. The activity source and meter are both named `Cntryl.Portia`; the
telemetry contract version is `2.0.0`.

Applications can subscribe directly, or reference the optional `Cntryl.Portia.Telemetry` package and add
all three signals to the standard OpenTelemetry hosting pipeline in one call:

```csharp
builder.Services.AddOpenTelemetry()
    .WithPortia()
    .WithTracing(tracing => tracing.AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddOtlpExporter())
    .WithLogging(logging => logging.AddOtlpExporter());
```

`WithPortia()` is idempotent. It registers Portia's source and meter and enables the standard
`ILogger` bridge; the application continues to own resources, sampling, filtering, exporters,
endpoints, and credentials. No other Portia package references OpenTelemetry.

## Trace contract and budget

Portia emits only three static activity names:

- `portia.request.send` (`Producer`)
- `portia.request.process` (`Consumer`)
- `portia.request.execute` (`Internal`)

Local and HTTP calls create one execute span. RPC creates a send -> process -> execute chain in one
trace. Queue, notice, and schedule producers end their send span before delivery; every delivery,
retry, or schedule firing starts a distinct root trace linked to the producer context, with at most
one process span and one execute child. Queue retries never parent their next attempt, notice fanout
does not create a wide producer trace, and recurring schedules never extend an earlier firing.
Linked deliveries remain roots even when an unrelated activity is ambient. Wire trace context is
marked remote at ingress so parent-based samplers distinguish received context from local activity.
MCP tools, resource reads, and prompt gets create one process span and one execute child, retaining
their local SDK parent when present. Their transport names are `mcp`, `mcp-resource`, and `mcp-prompt`;
resource templates and prompt names are startup-bounded labels.

Polling, reservation renewal, acknowledgement, retry delay, checkpoints, event processing, tenant
scans, reconciliation, assignment, and hosted-service lifecycle create no spans. Envelopes propagate
only W3C `traceparent` and optional `tracestate`; baggage is not propagated.

Activities use these ordered attributes:

- `portia.request.name`: startup-bounded generated discriminator
- `portia.transport.name`: `local`, `http`, `rpc`, `queue`, `notice`, `schedule`, or a bounded custom shape
- `messaging.system=fitz` and `messaging.operation.type=send|process` only when the adapter knows both facts
- `portia.outcome`: final bounded outcome

Portia never adds payloads, tokens, claims, request IDs, routes, or `RequestError.Message` to telemetry.
Requested cancellation records `canceled` without error status or an exception event; so does a
stream whose consumer stops enumerating early, unless disposing the stream then fails. A consumer
that throws while handling an item also stops early: the request itself did not fail, so the
consumer's failure belongs to the consumer's own telemetry (an HTTP stream records it as a runner
fault). Expected
`RequestError` values record their bounded kind and error status without the business message.
Unexpected failures, including unrequested cancellation, record `fault`, error status, and the full
exception event when the activity was sampled for data.
An unrelated exception remains a fault even if the caller token was also canceled. Stream
preflight exceptions retain their exception event and every started request balances the active count.
MCP process spans include projection and result limits; unexpected faults produce a correlated runner
fault log while client failures retain sanitized transport envelopes.

## Instruments

Durations use monotonic seconds. Hot-path measurements use fixed tag order and at most three
dimensions.

| Instrument | Kind | Unit | Dimensions |
|---|---|---:|---|
| `portia.request.duration` | Histogram | `s` | `portia.request.name`, `portia.transport.name`, `portia.outcome` |
| `portia.request.active` | UpDownCounter | `{request}` | `portia.request.name`, `portia.transport.name` |
| `portia.request.delivery.count` | Counter | `{delivery}` | `portia.request.name`, `portia.transport.name`, `portia.outcome` |
| `portia.authorization.duration` | Histogram | `s` | `portia.component.name`, `portia.stage`, `portia.outcome` |
| `portia.guard.duration` | Histogram | `s` | `portia.component.name`, `portia.outcome` |
| `portia.transport.operation.duration` | Histogram | `s` | `portia.transport.name`, `portia.operation`, `portia.outcome` |
| `portia.transport.trace_context.invalid` | Counter | `{request}` | `portia.transport.name` |
| `portia.aggregate.operation.duration` | Histogram | `s` | `portia.operation`, `portia.outcome` |
| `portia.aggregate.event.count` | Counter | `{event}` | `portia.operation` |
| `portia.event_store.operation.duration` | Histogram | `s` | `portia.operation`, `portia.scope`, `portia.outcome` |
| `portia.event_store.event.count` | Counter | `{event}` | `portia.operation`, `portia.scope` |
| `portia.processor.batch.duration` | Histogram | `s` | `portia.component.name`, `portia.runner.name`, `portia.outcome` |
| `portia.processor.event.count` | Counter | `{event}` | `portia.component.name`, `portia.runner.name` |
| `portia.processor.lag` | Histogram | `s` | `portia.component.name`, `portia.runner.name` |
| `portia.tenant_directory.operation.duration` | Histogram | `s` | `portia.operation`, `portia.phase`, `portia.outcome` |
| `portia.tenant_directory.event.count` | Counter | `{event}` | `portia.operation`, `portia.phase`, `portia.outcome` |
| `portia.tenant_directory.active.count` | Histogram | `{tenant}` | `portia.operation`, `portia.phase`, `portia.outcome` |
| `portia.workload.active` | UpDownCounter | `{workload}` | `portia.component.name`, `portia.scope` |
| `portia.worker.failure` | Counter | `{failure}` | `portia.runner.name`, `portia.stage` |
| `portia.worker.restart` | Counter | `{restart}` | `portia.runner.name`, `portia.stage` |
| `portia.fleet.assignment.active` | UpDownCounter | `{assignment}` | `portia.scope` |

Delivery outcomes are closed by transport. RPC records `completed` only after the response is
written. Queue records exactly one of `completed`, `abandoned`, `terminal`, `canceled`, or `fault`
per reservation. Notice and schedule record exactly one of `completed` or `lost`; malformed
envelopes and failed dispatches are lost.

Registered request and component names are startup-bounded. Never use tenant, aggregate, partition,
worker, request, correlation, or execution IDs; concrete routes; exception types or messages;
payload values; or arbitrary reasons as metric values. Processor lag is based on the last committed
event occurrence and clamped to zero. Only a committed batch records lag and processed events; a failed
batch records only its duration and outcome.

## Log catalog

| Event | Level | Meaning |
|---:|---|---|
| 1001 | Debug | fleet assignment lifecycle |
| 1002 | Error | swallowed unexpected background fault, with the full exception |
| 1003 | Debug | workload lifecycle |
| 1004 | Warning | irrecoverably lost one-way delivery |
| 1005 | Warning | terminal queue delivery returned unacknowledged to its transport |
| 1101 | Error | Fitz partition termination timeout |

Queue and notification runners retain their existing process span through disposition and cleanup
logging. Events 1002, 1004 and 1005 emitted for a dispatched delivery correlate to that process,
including under an unrelated ambient caller. Execute spans end when handler execution ends;
process duration additionally includes acknowledgment or abandonment, terminal callbacks and scope cleanup.
This extends an existing span's lifetime and adds no spans. The runner restores the caller's
ambient activity before processing another delivery. Public RequestDispatch still owns its own
process lifetime. Invalid envelopes are handled by the runner before dispatch.

Routine success, retry, and abandonment are silent. Expected actor or request failures are not
worker faults. Application exception text can enter event 1002, event 1101, and sampled trace
exception events; applications should therefore avoid secrets in exception messages and apply
their normal log and trace filtering policy.

Scheduled actor validation is the exception to silent retry: each thrown exception or transient
result records a validation-stage worker fault. A firing is tried at most three times, with one- and
two-second delays, and is recorded lost once after permanent rejection or exhaustion.

## Verified Prometheus dashboards

These expressions are qualified against **Prometheus 3.15.0** and the **OpenTelemetry OTLP
exporter 1.19.1**, using metrics-specific HTTP/protobuf export, cumulative temporality and
`UnderscoreEscapingWithSuffixes` translation. Other exporters/backends need their own name check.
The compiled [qualification workload](../bench/Portia.Benchmarks/Telemetry/PrometheusQualification.cs)
executes 321 real requests: 41 successes, 40 of each expected refusal kind, 40 in-handler caller
cancellations and 40 unexpected faults. It asserts exact exported counts, request/workload active
values of one then zero, and worker failure/restart counters of two. Worker and committed-batch
observations are controlled framework-instrument fixtures, separate from the real request bus.

Run `bash scripts/qualify-prometheus.sh artifacts/prometheus` after locked restore. This starts an
isolated, digest-pinned receiver on port 49090, waits for readiness with bounded polling, retains
raw samples/names/query JSON, and removes only its own container. No exporter wait enters an
operation performance measurement. [Configuration](../bench/Portia.Benchmarks/Telemetry/prometheus.yaml)
enables underscore translation; the receiver starts with `--web.enable-otlp-receiver`.

The tested C# configuration is:

```csharp
using var metrics = Sdk.CreateMeterProviderBuilder()
    .AddMeter("Cntryl.Portia")
    .AddView("portia.request.duration", new ExplicitBucketHistogramConfiguration
    { Boundaries = [0.001, 0.01, 0.1, 1, 10] })
    .AddView("portia.processor.lag", new ExplicitBucketHistogramConfiguration
    { Boundaries = [0.1, 1, 10, 60, 300] })
    .AddOtlpExporter((exporter, reader) =>
    {
        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        exporter.Endpoint = new Uri("http://127.0.0.1:49090/api/v1/otlp/v1/metrics");
        reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1000;
    }).Build();
```

The full endpoint includes `/api/v1/otlp/v1/metrics`; a signal-specific exporter must not be given
only the OTLP base URL. Explicit histogram buckets match this qualification. Portia's instrument
advice remains available for applications selecting their own wider production buckets.

| Panel | Verified PromQL |
|---|---|
| Throughput | `sum(rate(portia_request_duration_seconds_count[1m]))` |
| Request p95 | `histogram_quantile(0.95, sum by (le) (rate(portia_request_duration_seconds_bucket[1m])))` |
| Expected refusals (cumulative count) | `sum(portia_request_duration_seconds_count{portia_outcome=~"validation\|unauthorized\|forbidden\|not_found\|conflict"})` |
| Requested cancellations (cumulative count) | `sum(portia_request_duration_seconds_count{portia_outcome="canceled"})` |
| Unexpected faults (cumulative count) | `sum(portia_request_duration_seconds_count{portia_outcome="fault"})` |
| Mean observed commit age (cumulative) | `sum(portia_processor_lag_seconds_sum) / sum(portia_processor_lag_seconds_count)` |
| Commit-age p95 | `histogram_quantile(0.95, sum by (le) (rate(portia_processor_lag_seconds_bucket[1m])))` |
| Active requests/workloads | `sum(portia_request_active)` / `sum(portia_workload_active)` |
| Worker failures/restarts (cumulative) | `sum(portia_worker_failure_total)` / `sum(portia_worker_restart_total)` |

For operational refusal/cancellation/fault rates, apply `rate(...[1m])` to the selected count
series before summing. To split by operation, retain `portia_request_name` in the aggregation;
quantiles also retain `le`. Five minutes is a useful wider production window; this controlled
workload uses one minute. Counter panels grow monotonically; active UpDownCounters translate
into gauge-like series and are summed directly, never interpreted as counters.

Expected refusals and requested cancellation are separate from unexpected worker/infrastructure
faults. An optional broader non-success panel must be named explicitly; applications own alert
policy and should not count every refusal as a worker fault.

`portia.processor.lag` is a **histogram of event age observed at committed batches**. The qualified
mean is about five seconds; p95 is about 9.55 seconds because it is interpolated within explicit
histogram buckets, not an exact event-age gauge. For a rolling mean use rates of `_sum` and
`_count` with the same window. Idle or failed processors add no lag observations: a rolling mean
or quantile can be absent/NaN, while a cumulative mean retains historical observations. Neither
proves current backlog nor detects a stalled worker. There is no scalar `portia_processor_lag_seconds`
series to `max`; preserve the instrument's histogram semantics.

Bounded polling and exact raw samples establish the backend semantics, beyond query parsing.
MCP workloads are deferred with #137–#142. The release dependency check rejects prerelease
external direct and locked transitive NuGet dependencies; the existing transitive analyzer now
uses its stable 5.9.0 release.

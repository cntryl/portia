# Observability

Portia's core packages emit dependency-free diagnostics through `ActivitySource`, `Meter`, and
source-generated `ILogger` calls. The activity source and meter are both named `Cntryl.Portia`; the
telemetry contract version is `2.0.0`.

Applications can subscribe directly, or reference the optional `Portia.Telemetry` package and add
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

Polling, reservation renewal, acknowledgement, retry delay, checkpoints, event processing, tenant
scans, reconciliation, assignment, and hosted-service lifecycle create no spans. Envelopes propagate
only W3C `traceparent` and optional `tracestate`; baggage is not propagated.

Activities use these ordered attributes:

- `portia.request.name`: startup-bounded generated discriminator
- `portia.transport.name`: `local`, `http`, `rpc`, `queue`, `notice`, `schedule`, or a bounded custom shape
- `messaging.system=fitz` and `messaging.operation.type=send|process` only when the adapter knows both facts
- `portia.outcome`: final bounded outcome

Portia never adds payloads, tokens, claims, request IDs, routes, or `RequestError.Message` to telemetry.
Requested cancellation records `canceled` without error status or an exception event. Expected
`RequestError` values record their bounded kind and error status without the business message.
Unexpected failures, including unrequested cancellation, record `fault`, error status, and the full
exception event when the activity was sampled for data.

## Instruments

Durations use monotonic seconds. Hot-path measurements use fixed tag order and at most three
dimensions.

| Instrument | Unit | Dimensions |
|---|---:|---|
| `portia.request.duration` | `s` | `portia.request.name`, `portia.transport.name`, `portia.outcome` |
| `portia.request.active` | `{request}` | `portia.request.name`, `portia.transport.name` |
| `portia.request.delivery.count` | `{delivery}` | `portia.request.name`, `portia.transport.name`, `portia.outcome` |
| `portia.authorization.duration` | `s` | `portia.component.name`, `portia.stage`, `portia.outcome` |
| `portia.transport.operation.duration` | `s` | `portia.transport.name`, `portia.operation`, `portia.outcome` |
| `portia.transport.trace_context.invalid` | `{request}` | `portia.transport.name` |
| `portia.aggregate.operation.duration` | `s` | `portia.operation`, `portia.outcome` |
| `portia.aggregate.event.count` | `{event}` | `portia.operation` |
| `portia.event_store.operation.duration` | `s` | `portia.operation`, `portia.scope`, `portia.outcome` |
| `portia.event_store.event.count` | `{event}` | `portia.operation`, `portia.scope` |
| `portia.processor.batch.duration` | `s` | `portia.component.name`, `portia.runner.name`, `portia.outcome` |
| `portia.processor.event.count` | `{event}` | `portia.component.name`, `portia.runner.name` |
| `portia.processor.lag` | `s` | `portia.component.name`, `portia.runner.name` |
| `portia.workload.active` | `{workload}` | `portia.component.name`, `portia.scope` |
| `portia.worker.failure` | `{failure}` | `portia.runner.name`, `portia.stage` |
| `portia.worker.restart` | `{restart}` | `portia.runner.name`, `portia.stage` |
| `portia.fleet.assignment.active` | `{assignment}` | `portia.scope` |

Delivery outcomes are closed by transport. RPC records `completed` only after the response is
written. Queue records exactly one of `completed`, `abandoned`, `terminal`, `canceled`, or `fault`
per reservation. Notice and schedule record exactly one of `completed` or `lost`; malformed
envelopes and failed dispatches are lost.

Registered request and component names are startup-bounded. Never use tenant, aggregate, partition,
worker, request, correlation, or execution IDs; concrete routes; exception types or messages;
payload values; or arbitrary reasons as metric values. Processor lag is based on the last committed
event occurrence and clamped to zero.

## Log catalog

| Event | Level | Meaning |
|---:|---|---|
| 1001 | Debug | fleet assignment lifecycle |
| 1002 | Error | swallowed unexpected background fault, with the full exception |
| 1003 | Debug | workload lifecycle |
| 1004 | Warning | irrecoverably lost one-way delivery |
| 1005 | Warning | successfully terminalized queue delivery |
| 1006 | Warning | unsafe single-process workload-coordinator fallback |
| 1101 | Error | Fitz partition termination timeout |

Routine success, retry, and abandonment are silent. Expected actor or request failures are not
worker faults. Application exception text can enter event 1002, event 1101, and sampled trace
exception events; applications should therefore avoid secrets in exception messages and apply
their normal log and trace filtering policy.

## Dashboard starting points

- Rate: `rate(portia_request_duration_seconds_count[5m])`
- Errors: request duration rate where `portia.outcome != "success"`
- Latency: p50/p95/p99 grouped by `portia.request.name`
- Lag: max `portia_processor_lag_seconds` by `portia.component.name`
- Retries/failures: rate of `portia_worker_restart_total` and `portia_worker_failure_total`
- Active work: `portia_request_active`, `portia_workload_active`, and `portia_fleet_assignment_active`

Exporter-specific instrument-name normalization varies; verify the final names in the selected
backend.

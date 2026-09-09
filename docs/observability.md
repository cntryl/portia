# Observability

Portia emits dependency-free diagnostics through `ActivitySource` and `Meter`, both named `Cntryl.Portia` and versioned `1.0.0`. Applications choose their OpenTelemetry SDK/exporter and subscribe with `AddSource("Cntryl.Portia")` and `AddMeter("Cntryl.Portia")`.

## Trace contract and budget

Only three static names are emitted: `portia.request.send` (`Producer`), `portia.request.process` (`Consumer`), and `portia.request.execute` (`Internal`). Local and HTTP dispatch creates one execute span. An outbound adapter creates one send span. RPC, queue, notice, and scheduled delivery creates one process span and one execute child. A scheduled firing starts a fresh trace linked to the scheduling context, so recurring schedules cannot extend one trace indefinitely.

Portia follows W3C sampling flags and the application's listener. Envelopes propagate only `traceparent` and optional `tracestate`; baggage, credentials, claims, payloads, and business identifiers are excluded. Events, authorizers, polling, empty reads, batches, checkpoints, acknowledgements, retries, renewals, tenant scans, reconciliation, assignments, and host lifecycle never create spans. Background faults add an exception event only when an activity already exists.

Fleet partition callbacks receive cancellation when ownership is revoked. If a callback has not
stopped within `FleetRunOptions.PartitionStopTimeout` (30 seconds by default), the runner records
one `portia.worker.failure`, throws `FleetPartitionTerminationTimeoutException`, and its hosted
service stops the host. It does not start replacement work beside a callback known to be stuck.

Dashboards should key on these three stable span names and group by `request.type`.

## Instruments

Durations use monotonic seconds. Hot-path recordings have at most three tags in fixed order.

| Instrument | Unit | Dimensions |
|---|---:|---|
| `portia.request.duration` | `s` | `request.type`, `transport`, `outcome` |
| `portia.request.active` | `{request}` | `request.type`, `transport` |
| `portia.authorization.duration` | `s` | `component`, `stage`, `outcome` |
| `portia.transport.operation.duration` | `s` | `transport`, `operation`, `outcome` |
| `portia.transport.trace_context.invalid` | `{request}` | `transport` |
| `portia.aggregate.operation.duration` | `s` | `operation`, `outcome` |
| `portia.aggregate.event.count` | `{event}` | `operation` |
| `portia.event_store.operation.duration` | `s` | `operation`, `scope`, `outcome` |
| `portia.event_store.event.count` | `{event}` | `operation`, `scope` |
| `portia.processor.batch.duration` | `s` | `component`, `runner`, `outcome` |
| `portia.processor.event.count` | `{event}` | `component`, `runner` |
| `portia.processor.lag` | `s` | `component`, `runner` |
| `portia.workload.active` | `{request}` | `component`, `scope` |
| `portia.worker.failure` | `{request}` | `runner`, `error.type` |
| `portia.worker.restart` | `{request}` | `runner`, `stage` |
| `portia.fleet.assignment.active` | `{assignment}` | `scope` |

Registered request/component names are startup-bounded. Closed dimensions include transport, operation, scope, stage, outcome, error type, and runner. Never use tenant, aggregate, partition, worker, request, correlation, or execution IDs; concrete routes; exception messages; payload values; or arbitrary reasons as metric values. Outcomes are `success`, a known request-error kind, `rejected`, `abandoned`, `lost`, `canceled`, or `fault`. Processor lag uses the last committed event occurrence and is clamped to zero; checkpoints are intentionally absent.

## Log catalog

Event 1001 records fleet assignment lifecycle at Information, 1002 records swallowed background faults at Error with the exception, and 1003 records workload lifecycle at Information. Warnings cover rejection, dropped notice, abandonment, retry, and unsafe single-process fallback. Logs must not contain payloads, tokens, claims, business error messages, or concrete request routes. Routine successes are silent.

## Dashboard starting points

- Rate: `rate(portia_request_duration_seconds_count[5m])`
- Errors: request duration rate where `outcome != "success"`
- Latency: p50/p95/p99 histogram quantiles grouped by `request.type`
- Lag: max `portia_processor_lag_seconds` by `component`
- Retries/failures: rate of `portia_worker_restart_total` / `portia_worker_failure_total`
- Active work: `portia_request_active`, `portia_workload_active`, and `portia_fleet_assignment_active`

Exporter-specific name normalization varies; verify final names in the selected backend.

## Transport names

An inbound activity or metric names the *shape* of the delivery — `http`, `rpc`, `queue`,
`notice`, `schedule`, or `local` for an in-process call — supplied by the invocation itself
through `RequestInvocation.TransportName`. A custom transport supplies its own by deriving from
`RequestInvocation`; keep the value low-cardinality, since it is a metric tag.

Outbound `portia.request.send` activities are raised inside the adapter doing the sending and name
that adapter (`fitz.queue`, `fitz.rpc`, and so on). The asymmetry is deliberate: a producer knows
which technology it is using, while a consumer is describing the shape of what arrived and must
report the same value regardless of which adapter delivered it.

These names changed in the current unreleased version; see the [changelog](../CHANGELOG.md).

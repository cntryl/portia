# Breaking API corrections

This coordinated change contains breaking API and behavior corrections. It must
not be published as an ordinary compatible patch release. Package publication and
tagging are outside this PR; the manual publishing workflow is not invoked here.
A release must choose an appropriate breaking version and update its version policy
before publication.

## Aggregate persistence and stored events

- Register `AddPortiaAggregate<TAggregate>((services, id) => ...)`. The factory must
  return the requested explicit identity and define the aggregate's stable stream.
  Inject nongeneric `IAggregateRepository`; load/save methods remain generic.
- A pending save is exclusively raised events or audits. Both emission orders reject
  mixing before metadata creation, state application, or collection mutation.
- Raised events persist to the aggregate's source stream with physical OCC.
  Audits persist to a new UUIDv4 session stream per batch in the same realm/area.
  They retain aggregate ID and state version in metadata and do not advance the
  aggregate's source position. A failed audit save retains its session UUID for retry.
  Never route audit batches into the aggregate stream.
- `DomainEventMetadata.IsAudit` persists classification assigned by aggregate emission.
  Stored raised events without this field remain readable and default to `false`.
  There is no separate audit-event hierarchy. Historical records without audit
  classification cannot be retrospectively identified as audits by this default;
  do not infer classification from missing aggregate handlers.
- `Version` counts applied raised events. `CommittedStreamPosition` counts committed
  source-stream records. A successful raised save promotes its events into history;
  successful audits are cleared without entering that history. Failure preserves
  pending identities and versions. Empty saves append nothing.
- Loading reads only the aggregate's stable route. An audit-only session does not
  create an aggregate source stream, so loading an aggregate with no raised events
  returns null. Pattern readers and components can read audit sessions separately.
- Both concrete and pattern reads now return `DomainEventRecord`. Use `record.Ev`
  for the event and physical offsets for checkpoints. Read `fromOffset` is inclusive;
  append `expectedStreamPosition` is a count of committed records. Fitz pattern
  reads expose only the offset scopes the broker supplies; area/realm offsets may
  be null. Never use aggregate state version as a patterned checkpoint.
- Do not emit/save concurrently on one aggregate instance. OCC conflicts do not
  automatically rerun commands. Lifecycle mutation stays internal. Business tests
  use `Portia.Testing.AggregateScenario<T>` and `DomainEventSeed`; serializers use
  guarded one-time `DomainEvent.AttachMetadata`.

## Explicit modules and shared dispatch

Replace application calls to `AddPortiaGeneratedComponents()` with a named partial
`[PortiaModule]` in each feature assembly and `AddPortiaModule<TModule>()` in the host.
Use `[PortiaModule(typeof(ContractsModule))]` for intentional imports from separate
contract assemblies. Module registration composes handlers, authorizers, events,
transport registrations, and component descriptors without assembly scanning.
Repeated imports are idempotent; conflicts fail explicitly.

Remove references to an independently generated `RequestBus`. Resolve the shared,
scoped `IRequestBus`. Only the selected handler and authorizer are constructed;
nested dispatch uses the same scope. Permission checks still run first.

Replace `RegisterPortiaGeneratedRpcWorkersAsync()` with
`await server.RegisterModulesAsync(ct)`, retain its `IAsyncDisposable` handle, and
dispose it on shutdown. The server constructor now receives `IServiceScopeFactory`
in place of root-resolved bus, actor validator, and serializers. Each invocation
owns a fresh scope. Typed `RegisterAsync<T>()` remains available for explicit
single-request registration.

## Hosting and lifetimes

`AddPortiaProjectorRunner<T>()` now takes the **concrete projector type**, not its
projection-port type. Register the component's module first. Remove manual
`Projector<TProjection>` registration bridges. Multiple projectors sharing a port
and multiple reactors each get their own hosted worker.

Application dependencies for queue, notification and RPC deliveries resolve within
the delivery scope. Projector/reactor dependencies resolve within each pass. Keep
transport connections long-lived. Projectors reload the authoritative target
checkpoint on every pass and retain atomic projection/checkpoint batch ownership.

## HTTP contracts

Generated endpoints honor ASP.NET HTTP JSON options. Default naming is camel case;
applications relying on the old hardcoded snake case must explicitly configure
`JsonNamingPolicy.SnakeCaseLower`. Property converters, `JsonPropertyName`, JSON
metadata property names, and configured number handling apply to body values.
Nullable values and declared defaults preserve their contracts; invalid JSON roots,
wrong kinds, and missing required values return 400. Route/query scalar parsing is
invariant. Routes must be compile-time constants; `PORTIA016` describes unsupported
bindings. Queue HTTP dispatch requires a no-result request.

Use `.WithPortiaRouteValues(context => new RequestRouteValues(...))` to supply
contextual wildcard segments for `Prefer: respond-async`. Concrete segments remain
those declared by the request. Stream authorization failures return 401/403 before
the response begins; after-start failures abort rather than close a successful
JSON/SSE response. Streams enumerate once and dispose on cancellation/failure.

## Fitz queues and tenant lifecycle

The queue constructor's wait parameter is now `TimeSpan? waitDuration`, defaulting
to five seconds. It is rounded up to Fitz's whole seconds. The default reservation
batch is one item for sequential processing. Leases renew while processing and
while acknowledgment is pending; reservation loss cancels processing. Failure
stops renewal and does not acknowledge or republish the payload.

Fitz owns expiration, redelivery and dead-letter policy. Hosted consumers recover
from enumeration faults or unexpected completion using a one-second, cancellable
backoff through `TimeProvider`. This restarts subscriptions, not business commands.
Malformed payloads retain their reservation handle and do not terminate later work.
Expected terminal results retain existing acknowledgment behavior.

The pinned Fitz .NET 0.1.1 client supplies `Attempt = 1` for reserve responses;
Portia preserves the supplied value and does not invent a retry count. Tests prove
this passthrough with nondefault supplied values and prove real-broker handoff by
observing the same abandoned payload after expiration. Dead-letter activation
follows Fitz configuration. The Compose fixture has no threshold configured, so
these tests do not claim a live transition into a dead-letter queue.

`EventSourcedTenantDirectory` defaults to one-second polling with `TimeProvider`.
Each snapshot/watch has independent progress. A watch may begin by reconciling
current membership, including known removals, so consumers must handle duplicate
membership updates idempotently. It does not replay historical start/stop cycles
as live transitions. Failed active workloads restart after one second in a fresh
scope; removal and shutdown stop pending restarts.

See [the consumer fixture](../test/Portia.ConsumerTests/CompleteWorkflowTests.cs) and
[regression evidence](defect-remediation-evidence.md) for executable migration coverage.

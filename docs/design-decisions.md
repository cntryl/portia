# Design decisions

This page records why Portia's less-obvious boundaries exist, what the framework enforces, and
what remains application-owned. It describes current behavior rather than a future roadmap.

## Batching is the normal processor path

Projectors and reactors process one ordered batch path. Single-event bases select batches of one;
batch bases select a configured bound. This avoids separate correctness paths while retaining an
explicit commit boundary. Generated dispatch and processor tests enforce ordering and checkpoint
behavior. Applications choose a batch size based on how many backend operations each event causes.

## Reactors model effects, not only commands

A reaction may dispatch a command or invoke an integration gateway directly. Commands are the
best fit for reusable application behavior that benefits from Portia's request handler and
authorization pipeline. Direct calls are the better fit for effects inherently caused by the
event. `SendReactionAsync` preserves causal context and converts a failed command result into a
reactor fault. In either form, the application must make the effect safe to replay because effect
completion and checkpoint persistence are not atomic.

## Fitz notifications are wakeups

Fitz stores the authoritative event stream and can notify processors that work may be available.
Notifications reduce latency; they are not proof that the local view is current. Portia rereads
durable state and retains bounded reconciliation so reconnects, notification loss, and broker
restart do not become correctness failures.

## Authentication boundaries differ; authorization does not

HTTP establishes a principal through ASP.NET Core. Token-bearing Fitz transports validate the
carried credential when work executes. Both then dispatch through `IRequestBus`, where the same
permission and authorizer pipeline runs. Adapters may authenticate differently but cannot define
an alternative authorization path.

## Fencing is enforced where writes become durable

Fleet leases supply monotonic fencing tokens and Portia carries the active token in
`WorkloadContext`. Only the application repository knows how its durable target performs a
conditional write, so it owns the comparison. `FencingTokenConformance` turns that responsibility
into executable proof. If revoked partition work ignores cancellation, Portia fails the runner and
host after a bounded interval instead of knowingly allowing replacement work beside it.

## Tenant scope and fleet placement are independent

Tenant scope decides which logical tenant workloads exist. Fleet placement decides which worker
owns each resulting workload. Keeping those axes independent prevents deployment topology from
becoming an authorization or storage-partitioning rule. Workload and fleet tests cover addition,
removal, redistribution, and retained ownership.

## Aggregates stay bounded; snapshots are not supported

Portia intentionally has no aggregate snapshot contract and none is planned. Aggregates should
model bounded consistency boundaries that can rehydrate directly from their event streams.
Projection rebuild generations are separate from aggregate hydration and do not introduce
aggregate snapshots.

## Delivery evidence

The normal test suite covers in-process contracts, public consumers, reflection-disabled JSON,
and Fitz integration through Docker Compose. `Portia.Testing` provides reusable conformance suites
for application persistence. A backend or platform scenario that did not run is unverified, never
reported as passing. See [scope](scope.md) for the current support boundary.

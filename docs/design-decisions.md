# Design decisions

This page records why Portia's less-obvious boundaries exist, what the framework enforces, and
what remains application-owned. It describes current behavior rather than a future roadmap.

## Batching is the normal processor path

Projectors and reactors process one ordered batch path. Single-event bases select batches of one;
batch bases select a configured bound. This avoids separate correctness paths while retaining an
explicit commit boundary. Generated dispatch and processor tests enforce ordering and checkpoint
behavior. Applications choose a batch size based on how many backend operations each event causes.

## Projectors are pure; reactors cause effects

A projector's writes and its checkpoint commit atomically, so an effect it causes outside that
transaction is repeated on every failed commit and every rebuild generation. A projector
therefore reads events and writes its own projection and nothing else: no command dispatch, no
HTTP call, no publish, no enqueue. Reactors exist for exactly the effects that cannot join the
projection transaction, and are checkpointed separately because of it. `PORTIA100` warns when a
projector takes a known effect dependency. It deliberately remains a best-effort heuristic rather
than claiming to prove arbitrary application behavior.

## Read-model storage is first-party optimized and userland pluggable

Portia owns projection execution and its atomic checkpoint contract, not a universal storage or
query abstraction. Cassie is the intended first-party read-model engine for native SQL, graph,
time-series, and vector models. It must implement the same public projection boundary available to
an application-owned PostgreSQL, Snowflake, or other repository; no Cassie-only runner or weakened
checkpoint semantics are permitted.

An application repository implements `IProjectionStore`, participates in an `IProjectionBatch`,
and uses its backend's native client, schema, and query model. If its data mutations and checkpoint
cannot commit atomically, the integration is an at-least-once reactor effect instead of a
transactional projector. This distinction keeps additional stores easy to add without reducing all
read models to a lowest-common-denominator DSL. The accepted three-layer product boundary and its
delivery gates are recorded in [platform vision](platform-vision.md).

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

## Guards are preflight; aggregates remain authoritative

Authorization decides whether the actor may attempt an operation. Pipeline behaviors wrap an
operation. A unary `IRequestGuard<TRequest>` instead checks asynchronous surrounding state at the
innermost handler boundary and can return an ordinary expected failure. Keeping those roles
separate makes the execution order explicit: authorization, behaviors, guards, then handler.

A guard may use a read model to reject work cheaply, but that read can become stale immediately.
Domain policies therefore remain in application aggregates and value objects, and optimistic
concurrency remains authoritative. Portia supplies no domain-policy DSL and does not cache guard
outcomes across retries. Streamed requests run the same guard stage before their first item; an
outer behavior that does not invoke its continuation skips both guards and the handler.

## Fail-closed authorization is a composition decision

`RequireAuthorization()` is opt-in at the composition root, so existing applications keep their
behavior and request contracts stay free of authorization attributes. Public requests are named
there with `AllowAnonymous<T>()`, which accepts request families. An unprotected request under the
requirement is a composition mistake rather than an access decision, so hosted startup rejects it and
dispatch throws instead of returning `Forbidden`, which would disguise the mistake as a denial.

## The executor owns mechanics; the handler owns the decision

Aggregate methods decide what happened and produce pending records. The handler's operation chooses
the caller's `Result` and, independently, whether everything the operation produced commits or is
discarded. `IAggregateExecutor` hydrates, invokes, and carries out that one decision atomically; it
never selects the aggregate, interprets the result, retries a conflict, or coordinates aggregates.
An operation either changes state or audits, never both, so each commit is one atomic stream write.
The repository stays a persistence abstraction.

## MCP is another Portia ingress, not another application model

Applications declare an MCP tool beside the request handler with `AddMcpTool<TRequest>()` and
activate that shared catalog at a host boundary with either `AddMcpStdio()` or the explicit
`AddMcpHttp()` plus `MapPortiaMcp()` pair.
There is no second composition root, MCP-specific handler, or runtime assembly scan. `ICallable`
remains the remote request-response opt-in; the request discriminator supplies the stable default
tool name; its XML summary supplies the model-facing description; and the existing application JSON
shape is the entire input. Explicit metadata overrides presentation only and never authorization.

An invocation binds the generated request and enters the same `IRequestBus` pipeline used by other
Portia receivers. HTTP authentication or an explicit stdio `IMcpActorProvider` owns identity.
`RequestRoute` continues to describe routing transports and does not manufacture MCP parameters.
The optional packages own tools only: resources, prompts, sampling, elicitation, streaming requests,
queues-as-tasks, and schedules-as-tools remain unsupported until they have native Portia semantics.

## Fitz owns distributed workload leases

Portia uses Fitz lease inventory to assign work across the fleet and holds a Fitz lease around each
projector or reactor run. It does not reinterpret Fitz's broker-local lease token as a durable
application fencing protocol. If revoked partition work ignores cancellation, Portia fails the
runner and host after a bounded interval instead of knowingly allowing replacement work beside it.

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

Event-store appends are atomic for one aggregate stream. Cross-aggregate workflows use events and
reactors with at-least-once replay safety; Portia does not add a multi-stream transaction or a
transactional request outbox. Archival cannot renumber a retained suffix because aggregate
hydration requires original contiguous offsets. Where a history no longer represents a bounded
consistency boundary, model an explicit domain rollover to a new aggregate instead.

## Delivery evidence

The normal test suite covers in-process contracts, public consumers, reflection-disabled JSON,
and Fitz integration through Docker Compose. `Cntryl.Portia.Testing` provides reusable conformance suites
for application persistence and for `IEventStore` itself. A backend or platform scenario that did not run is unverified, never
reported as passing. See [scope](scope.md) for the current support boundary.

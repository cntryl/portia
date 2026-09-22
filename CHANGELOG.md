# Changelog

Notable changes to Portia. Entries call out anything that changes observable behavior for an
application already running on a previous version, including telemetry, since dashboards and
alerts are as breaking to change as an API.

## Unreleased

### Fixed

- `SingleProcessWorkloadCoordinator` now cancels and awaits every remaining workload when a
  workload faults. When more than one workload faults together, `RunAsync` fails with an
  `AggregateException` holding every fault instead of only the last one; a single fault is still
  rethrown as-is.
- `EventSourcedTenantDirectory` bounds each commit-notification wait by its poll interval, so a
  notification lost to a reconnect or bounded subscription buffer no longer stalls tenant discovery
  until the next lifecycle commit.
- `FitzScheduledRequestConsumer` now records a firing as lost, without validating its system
  identity or dispatching it, when the embedded request's declared schedule route does not resolve
  to the fired route. Previously an entry could run another route's schedulable request as the
  principal approved for the fired route.
- A fired schedule entry that `FitzScheduledRequestConsumer` cannot translate now also records a
  `validation`-stage runner fault naming only the failure type, so every lost firing has a logged
  reason. Previously only the generic lost-delivery warning was emitted.
- Disposing a Fitz event-store subscription whose pending wait faulted now releases the Fitz
  subscription instead of rethrowing that fault and leaking it.
- `FitzEventStore` pattern reads now fault on a record whose stream metadata disagrees with the
  broker-reported record route.

## 0.5.5 - 2026-09-22

### Fixed

- OpenAPI export now preserves the `string`/`uuid` item schema for collections of `Uuid` values.

## 0.5.4 - 2026-09-21

### Fixed

- `FitzEventStore` now normalizes Fitz append-session admission contention into
  `EventStreamConcurrencyException` before a session is acquired. Stale-position contention after
  acquisition remains separately classified, and Portia does not retry business commands.

### Changed

- Updated the synchronized Cntryl.Fitz package family to 1.4.2, which exposes named constants for
  stale stream positions and active append-session contention.

## 0.5.3 - 2026-09-21

### Added

- Tenant-directory replay, catch-up, and cursor diagnostics report bounded read duration, event
  volume, active-tenant counts, and safe outcome tags without exposing tenant or cursor identities.

### Fixed

- OpenAPI export preserves the type, UUID format, nullability, and null default of optional nullable
  UUID record parameters.
- Domain-event duplicate-discriminator analysis no longer treats nullable and non-nullable spellings
  of one CLR event type as separate registrations.
- Commit-time aggregate concurrency conflicts now remain safe transient conflicts across HTTP and MCP
  ingress, without automatic command retries or storage-detail disclosure.
- MCP tools now bind nested object and array arguments through their source-generated request metadata.

### Changed

- Refreshed compatible direct NuGet dependencies and lock files.

## 0.5.2 - 2026-09-18

### Changed

- A hosted projector whose `FitzKvProjectionStore` repository was constructed for a different
  projector name now stops the host when it starts, naming both, instead of failing its first
  checkpoint load and retrying until the consecutive-failure limit. A repository driven outside
  hosting still throws on its first checkpoint load.
- Updated the Portia Fitz adapter and its packed integration consumer to the synchronized Cntryl.Fitz
  1.4.0 package family. Fitz 1.4.0 adds `IKvTransaction.Route` and a
  `KvDirectory<T, TKey>.QueryAsync` overload that takes an open transaction, so a
  `FitzKvProjectionStore` repository can page a `Cntryl.Fitz.Extensions` directory through the
  transaction `BeginReadAsync` returns. An application that references `Cntryl.Fitz.Extensions`
  1.4.0 must also run `Cntryl.Fitz.Core` 1.4.0 — the 1.3.x transaction does not report its route,
  so that overload throws `NotSupportedException` — which this update supplies transitively.

## 0.5.1 - 2026-09-18

### Breaking

- `FitzKvProjectionStore` is now constructed with the name of the one projector it serves — the ID
  the projector is registered under with `AddProjector` — and throws on a checkpoint load or batch
  for any other workload name. A repository whose projector is registered under a different name now
  fails on its first checkpoint load instead of committing to a resource its reads never open.
- Removed `FitzKvProjectionStore.RouteFor`, added in 0.5.0. Hand-supplied component names and realms
  silently derived an empty resource on any mismatch. Query-side reads now open their transaction
  through the protected `BeginReadAsync(realm)`, which derives the resource from the repository's own
  projector name.

### Added

- `FitzKvProjectionStore.BeginReadAsync(realm)`, which opens a read-only transaction on the resource
  the repository's projector writes for one realm. It throws while a batch is open on the same
  instance, because it cannot see that batch's staged writes; reads during a batch go through
  `Transaction`.

## 0.5.0 - 2026-09-18

### Breaking

- `FitzKvCheckpointStore` and `FitzKvProjectionStore` now treat their configured route as a base and
  transact against one derived Fitz KV resource per workload: one component in one realm, so each
  tenant of a `PerTenant` reactor or projector has its own resource. Fitz KV locks a whole resource
  for a read-write transaction's lifetime, so before this change every reactor behind
  `UseKvCheckpoints`, every tenant of one per-tenant component, and every projector given the same
  route contended for one lock. Under load those conflicts reached the consecutive-failure limit and
  stopped the host. There is no migration: after upgrading, every reactor replays from the start of
  its pattern once and every projection rebuilds once, so reactor effects must be replay-safe before
  you upgrade.
- Query-side reads of a `FitzKvProjectionStore` repository's data must now open their transaction
  on `FitzKvProjectionStore.RouteFor(route, componentName, realm)` instead of the base route.

### Added

- `FitzKvProjectionStore.RouteFor`, which names the resource a projector workload's data and
  checkpoint live in.

### Changed

- Reorganized source, test, benchmark, and smoke-consumer projects by capability, with shared
  global usings and clearer project boundaries.
- Documented the formatter requirements used by the repository and CI.

## 0.4.4 - 2026-09-17

### Fixed

- OpenAPI schemas now describe Portia `Uuid` values as strings with the `uuid` format, including
  nested request and response properties, route parameters, and direct response values. Generated
  clients no longer encounter empty, untyped schemas for UUID-backed fields.

### Changed

- Updated the Portia Fitz adapter and packed integration consumer to the synchronized Cntryl.Fitz
  1.3.0 package family.

## 0.4.3 - 2026-09-17

### Changed

- Updated the Portia Fitz adapter and its packed integration consumer to the synchronized
  Cntryl.Fitz 1.2.0 package family.

## 0.4.2 - 2026-09-17

### Added

- `AggregateOutcome.CommitOnSuccess(result)` removes the routine commit/discard ternary by committing
  successful results and discarding failed results. Untyped and value-returning operations are both
  supported; exceptional policies such as committing a failed audited operation remain explicit through
  `AggregateOutcome.Commit(result)`.

## 0.4.1 - 2026-09-17

### Changed

- Updated the Portia Fitz adapter to the synchronized Cntryl.Fitz 1.1.0 package family. Fitz
  remains byte-oriented; applications can pair it with Cntryl.LexKey 0.1.2 and pass
  `LexKey.AsMemory()` without copying.

## 0.4.0 - 2026-09-16

### Added

- `PORTIA025` is registration-driven and accepts JSON roots advertised by referenced assemblies.
  `[PortiaJsonContext]` now emits editor-hidden assembly markers and AOT-safe context factories, so
  endpoints and MCP tools can reuse contract metadata without duplicating `[JsonSerializable]` roots;
  runtime composition still rejects any advertised root that the composed resolver chain cannot serve.

- `EventStreamPattern.ForTenant(area, resource)` declares an unbound tenant workload template.
  Per-tenant workloads require templates, global workloads require exact patterns, the reserved
  template realm cannot be recreated through `ForPattern`, and manual runners reject unbound templates.

- `Cntryl.Portia.Mcp.Testing` provides an assertion-framework-neutral `McpScenario` over the official
  Streamable HTTP client. It snapshots tool catalogs, schemas, hints, metadata, text, and structured
  results while preserving authentication on the caller-owned `HttpClient`.

- `IRequestGuard<TRequest>` adds reusable asynchronous preflight for commands and result-bearing
  queries. Generated `AddRequestGuard<T>()` registrations are scoped and reflection-free, match
  concrete requests or application-owned request-family interfaces by assignability, and execute
  in registration order inside pipeline behaviors immediately before the handler. The first
  ordinary `Result` failure short-circuits handling; query failures retain the same `RequestError`,
  successful guards preserve the handler result, and fresh queue redeliveries rerun fresh scoped
  guards. Streamed requests run matching guards before their first item and end with
  `RequestGuardException` on failure, which HTTP streaming maps to a problem response. Each guard
  records `portia.guard.duration`, and one type cannot be both an authorizer and a guard.

- `PortiaBuilder.RequireAuthorization()` makes authorization fail closed at the composition root.
  Every registered request then needs an applicable authorizer, `[RequiresPermission]`, or
  `AllowAnonymous<T>()` (concrete request or family). Hosted startup lists unprotected requests and
  dispatch refuses them; applications that do not opt in are unchanged.

- `IAggregateExecutor` executes one operation against one hydrated aggregate. The handler returns
  `AggregateOutcome.Commit(result)` or `AggregateOutcome.Discard(result)`, deciding the caller's result
  and the persistence of everything the operation produced independently; Portia hydrates, invokes,
  and commits atomically, never retries conflicts, and invalidates an instance whose pending records
  were discarded. `IAggregateReader` and `IAggregateWriter` expose the repository's read and write
  capabilities, and `AddPortia()` registers all of them.

- `PORTIA105` and `PORTIA106` warn when a request guard or authorizer takes a known effect-capable
  dependency, and `PORTIA100`/`PORTIA102` recognize the new aggregate writer, reader, and executor.

- `RequestScenario` in `Cntryl.Portia.Testing` runs a request through the real lifecycle and asserts
  authorization, guards, handling, results, values, and stream items with an immutable, awaitable
  expectation chain. `Cntryl.Portia.Testing` now depends on `Cntryl.Portia.Core`.

- Generated HTTP endpoints bind each request member by cascading route, body, then query string,
  so POST/PUT/PATCH members the body omits can come from the query without request-type attributes.
  Scalar-only bodies bind from `application/x-www-form-urlencoded` and `multipart/form-data` forms
  as well as JSON, using the JSON wire names, and OpenAPI describes both form content types. A POST
  whose scalar members all arrive in the query no longer requires a body. `FromQuery(x => x.Member)`
  on the mapping binds a member only from the query string and documents it as a query parameter. Form posts validate antiforgery tokens, and without a
  registered antiforgery service they return 415 and OpenAPI omits the form content types unless
  the endpoint calls `DisableAntiforgery()`, so default form binding cannot open a cross-site
  request forgery path. `MapPortia*` `configure` overloads add an `OnBind` escape hatch for custom request
  binding and an `OnResult` hook for post-operation HTTP handling such as cookies, headers, and
  redirects; a queuable endpoint with `OnResult` stays synchronous and ignores `Prefer: respond-async`.
  Declared custom bodies are completely size-validated before `OnBind`, including chunked requests.

- Optional `Cntryl.Portia.Mcp` and `Cntryl.Portia.Mcp.AspNetCore` packages expose explicitly
  selected `ICallable` requests as generated MCP tools over stdio or stateless Streamable HTTP.
  `AddMcpTool<TRequest>()` stays in the shared Portia composition root, derives names, descriptions,
  and JSON Schemas from existing request metadata, and dispatches through the normal `IRequestBus`
  actor, authorization, pipeline, result, cancellation, and diagnostics boundaries. HTTP activation
  composes with standard endpoint conventions; stdio requires an explicit actor policy.

- MCP hosting now keeps stdio protocol output free of console logs, emits object-shaped structured
  results for wire compatibility, validates tool names and descriptions, preserves bounded error
  kinds in telemetry, and records unexpected faults without disclosing them to clients. HTTP
  activation is explicit through `AddMcpHttp()`, honors `PortiaHttpOptions.MaxJsonBodyBytes`, and no
  longer alters unrelated `WebApplication` builds. XML summaries retain common documentation markup
  and inherit documentation when source is available, with a stable fallback for contracts
  assemblies that do not publish XML documentation.

- Document the accepted platform boundary: Fitz is the authoritative event and distributed
  fabric, Portia owns application and projection execution semantics, Cassie is the first-party
  SQL/graph/time-series/vector read-model engine, and PostgreSQL, Snowflake, or another backend
  remains an ordinary userland `IProjectionStore` integration rather than requiring a Portia fork
  or generic storage DSL.
- The optional `Cntryl.Portia.Telemetry` package adds idempotent `OpenTelemetryBuilder.WithPortia()`
  registration for Portia traces, metrics, and the standard `ILogger` bridge. Applications retain
  ownership of exporters, resources, sampling, filtering, endpoints, and credentials; no existing
  Portia package gains an OpenTelemetry dependency.

- The package smoke consumers map the local package folder in their own `packageSourceMapping`.
  The repository root maps every package to nuget.org and a nearer mapping replaces rather than
  extends it, so a cold restore could not see the freshly packed smoke packages and resolved a stale
  version or failed outright; the step only passed against a warm global package cache.
- Every package declares `PackageTags`.
- Portia is licensed under the Apache License, Version 2.0. Every packable project declares the
  `Apache-2.0` SPDX expression, so a consumer's license scanner resolves the terms from the package
  itself, and packing now fails if a packable project declares no license at all.

### Fixed

- Oversized unknown-length form bodies still return 413 when antiforgery performs the first form
  read and wraps the body-limit exception. Configured HTTP routes validate the route builder's
  actual endpoint sources before Portia startup schedules and workers can act; route groups retain
  their complete ancestor prefix during validation.
- `PORTIA018` rejects guards scoped directly to `IStreamRequest<T>` as well as concrete and
  application-owned stream-only request types, instead of accepting a guard that streaming
  dispatch can never execute.
- Queue workers reject a zero terminal attempt, and Fitz workers reject any configured terminal
  threshold before connecting because Fitz 1.0 does not expose durable attempt counts. Malformed,
  incomplete, wrong-kind, invalid-metadata, and invalid known-contract queued envelopes use
  `DeserializationFailure` only when a terminal handler exists; otherwise they remain transport-owned.
  Only structurally valid unsupported envelope versions, unknown contract/version pairs, and
  unclassified read failures remain retryable,
  reaching `RetryLimitReached` only at a durable threshold with a handler. Failed lazy fields are not reread.
- Workload registration names are the stable hosted projector/reactor ownership, checkpoint, and
  effect identities; constructor-selected names remain defaults for manually driven components.
  Fitz requires an application name or explicit fleet configuration only when the effective
  coordinator is Fitz, so request-only listeners and custom-coordinator workloads remain valid unnamed.
- Scheduled Fitz workers require an application `IScheduledRequestActorValidator`, resolved from a
  fresh dependency-injection scope for every attempt. Definite rejection drops the firing; thrown
  and transient-result failures make at most three attempts with cancellable one- and two-second
  host-clock delays. Each failed attempt is observable, exhaustion loses only that firing, and later
  route notifications continue. Retries wait off the route's read loop (at most 32 pending per route),
  so healthy firings are not delayed behind a retrying one and may be delivered ahead of it.
  Asserted route, subject, and issuer values no longer mint a system principal directly.
- Reactor checkpoint writes use a private compare-and-save path when backed by Fitz KV, preventing a
  stale worker from overwriting newer progress without changing the public checkpoint contract.
- Authenticated HTTP requests using `respond-async` require a portable Bearer credential. Synchronous
  cookie/API-key requests, Bearer queue dispatch, and authorized anonymous queue dispatch retain
  their existing behavior.
- `PORTIA029` reports invalid custom transport IDs at compilation. Transport generator inputs compare
  transport lists structurally, nullable null defaults are omitted from OpenAPI schemas, bare-root
  recursive MCP references are rewritten, and the partial-component code fix preserves declaration
  trivia under warnings-as-errors.
- Publishing separates read-scoped verification and immutable package preparation from the
  write-scoped tag/package job. The exact-SHA tag is established and read back before package push.

- CI restores the locked dependency graph explicitly before formatting, and the lock files match
  the currently published `Cntryl.Fitz.Core` 1.0.0 package. The build SDK is exact so a newer patch
  cannot silently change SDK-injected dependencies underneath that locked graph. Package drift now
  fails in the restore step instead of surfacing only as a formatter failure. CI also packs the
  shipping packages, publishes a fresh external ASP.NET Core consumer with NativeAOT, and executes
  its HTTP and OpenAPI assertions.
- Domain-event serialization no longer builds intermediate JSON object trees on the current-schema
  path, projector and reactor passes reuse immutable per-pass state, and aggregate hydration
  pre-sizes its event-ID set. The optimized envelope writer can choose different legal JSON string
  escapes, but preserves the durable fields and values and reads envelopes written by the previous
  implementation.
- Portia diagnostics now require semantic evidence and say exactly what is wrong. `PORTIA025`
  ignores consumer methods that merely share Portia API names, `PORTIA012` recognizes referenced
  domain events used by the application, `PORTIA104` ignores filtered catches and paths that
  rethrow unexpected failures, and duplicate discriminator/unsupported-shape diagnostics identify
  both the source location and the concrete cause. The JSON metadata code fix creates a valid,
  collision-free context beside the missing type, and project Fix All adds every root in one edit.
- An idle server-sent-event stream emits keep-alive comments. A quiet stream is the normal state of
  an event source, and an idle connection is what proxies, load balancers and browsers reclaim, so a
  `text/event-stream` response that said nothing between events was being closed underneath the
  caller. `PortiaHttpOptions.ServerSentEventKeepAlive` sets the interval, or `null` disables it.
  Event framing no longer rewrites and splits the whole payload per item; a payload with no line
  break — the usual case — is written once.
- `PORTIA027` no longer depends on the order endpoint conventions are written in.
  `.WithTags("x").ExcludeFromDescription()` reported a duplicate operation ID that
  `.ExcludeFromDescription().WithTags("x")` did not, because only the call directly attached to the
  mapping was examined. The whole builder chain is now walked. An exclusion applied to a variable
  later is still not detected; the runtime document check remains the backstop.
- An idle Fitz queue consumer no longer throws a `TimeoutException` per poll. The notification
  backstop elapsing is the ordinary state of an idle queue, and waiting on it by catching a timeout
  meant one thrown exception every few seconds per consumer, forever.
- The test fixtures that need the Compose-managed Fitz broker say so when it is absent, instead of
  failing with a WebSocket EOF or an authentication error from inside the client. `CONTRIBUTING.md`
  now states the dependency and the broker's configuration.
- CI cancels superseded runs on a ref, bounds the job with a timeout, caches NuGet by lock file, and
  waits for the Fitz container instead of racing the first test against a broker that is still
  starting.

- Generated HTTP endpoints are described again. Mapping the handler as a `RequestDelegate` keeps
  `RequestDelegateFactory` out of a consumer's AOT build but adds no `MethodInfo`, and ApiExplorer
  describes only endpoints that carry one, so every Portia route had silently disappeared from
  `/openapi/v1.json`. The handler's metadata is now supplied explicitly, and both package smoke
  consumers assert the document names their mapping.
- `Prefer: respond-async` authorizes before it accepts. The queue pivot previously enqueued without
  consulting authorizers or `[RequiresPermission]`, so an unauthenticated caller received 202 and a
  durable queue write for any `ICallable`+`IQueuable` request, with the refusal arriving later as a
  dead letter. `IRequestBus.AuthorizeAsync` is the shared check; the worker still re-validates the
  carried actor token and authorizes again when it runs the request.
- Fitz route segments are ASCII and bounded. `char.IsLetterOrDigit` is Unicode-aware, so a Cyrillic
  "а" produced a route indistinguishable to a reader from its Latin counterpart while addressing
  something else — a confusable tenant realm.
- `JwtRequestActorValidator` no longer returns Microsoft.IdentityModel's own diagnostics to the
  caller; those name the configured issuer, audience, signing key and server clock, and the message
  travels into wire outcomes and logs. It also observes its cancellation token.
- A JSON request body no longer reserves memory for a `Content-Length` the caller never sends. The
  buffer hint is capped, and the read chunk is pooled rather than allocated per request.
- Partition and membership retries back off exponentially with jitter to a 30-second ceiling.
  A flat one-second delay retried an entire fleet in lockstep against a broker that had just failed.
- A queue reservation's renewal failure is published and read through `Volatile`, so acknowledging a
  reservation whose lease was already lost cannot miss it.
- Fitz queue workers require an application `IQueuedRequestTerminalHandler` and reject a positive
  `QueueRunnerOptions.TerminalAttempt` during startup, before connecting. Fitz 1.0 reports
  `QueueItem.AttemptUnavailable` for every queue delivery, so accepting the threshold left retryable
  failures in an unbounded redelivery loop that could never reach the configured attempt.
- All queue runners now validate an application-selected terminal handler in a disposable scope before
  transport enumeration and revalidate each delivery scope. Portia supplies no default policy that can
  silently acknowledge poison messages.
- Unsupported integer request-envelope versions are classified as retryable immediately after reading
  `version`, without interpreting fields owned by that unsupported format.
- Fleet partition and membership acquisition now fails fast on contention and uses Portia's bounded,
  jittered retry loop. Fitz serializes acquisitions per client, so waiting inside one contended acquire
  blocked every partition behind it and prevented healthy scale-out and takeover from converging.

### Changed

- Generated HTTP endpoints and the MCP Streamable HTTP endpoint refuse `POST`, `PUT`, `PATCH`, and
  `DELETE` requests that a browser reports as cross-origin (`Sec-Fetch-Site`, or without it an
  `Origin` that does not match the request host) with `403 application/problem+json` before
  binding, unless the application's ASP.NET Core CORS pipeline allows that origin for the endpoint.
  Portia records the decision the CORS service makes, so `UseCors("policy")`, `RequireCors`, the
  default policy, and `[DisableCors]` apply as configured. Requests without browser origin headers
  are unaffected. Browser applications served from another origin, including a subdomain, must now
  be allowed by a CORS policy.

- `MapPortiaGet`, `MapPortiaPost`, `MapPortiaPut`, `MapPortiaPatch`, `MapPortiaDelete`, and the
  streaming variants require `AddHttp()` and throw while mapping without it.

- Default HTTP body binding reads JSON only from requests that declare a JSON media type
  (`application/json` or a `+json` type). A `text/plain` or other non-JSON body, a form posted to a
  body with complex members, an empty body declaring a non-JSON type, or a body without a content
  type now returns `415 application/problem+json` instead of binding, and OpenAPI documents the 415
  response. Browsers send those bodies cross-site without a CORS preflight, so a page could forge a
  JSON request around antiforgery. Clients must send `Content-Type: application/json`; requests
  without a body are unaffected.

- Remove the combined `IAggregateRepository` contract and public `AggregateRepository`
  implementation. Inject `IAggregateReader`, `IAggregateWriter`, or `IAggregateExecutor` according
  to the capability a component needs; `AddPortia()` backs the reader and writer with the same
  scoped internal repository. Source and binaries that referenced either removed type must migrate
  and be recompiled.

- The hosted startup error for a missing `IPermissionEvaluator` now says "permission-protected request
  types" instead of "guarded request types", so it no longer suggests `IRequestGuard`.

- **Telemetry 2.0.0:** replace the unreleased request telemetry schema without compatibility
  aliases. Request activities now use `portia.request.name`, `portia.transport.name`, and
  `portia.outcome`; known Fitz operations additionally emit standard `messaging.system` and
  `messaging.operation.type`. `RequestInvocation` now requires an explicit parent-or-link trace
  relationship and may name its messaging system. The low-level activity helpers now take bounded
  request and transport facts, and `portia.request.delivery.count` records a typed final delivery
  outcome. RPC remains one parented trace; queue, notice, and schedule attempts are distinct linked
  roots. See [observability](docs/observability.md) for the replacement contract and log catalog.

- Replace `Cntryl.Fitz` with `Cntryl.Fitz.Core` 1.0.0 and update
  `Cntryl.Fitz.Abstractions` from 0.1.3 to 1.0.0, then
  update the Compose broker digest alongside them. Portia now consumes Fitz's unified
  `Cntryl.Fitz` namespace, `TimeSpan` duration API, memory-backed payloads, and
  `ScheduleDeliveryMode.Once` name. Public Portia signatures that expose Fitz types consequently
  use their 1.0 namespace identities.
- The OpenAPI document is composed once and served as rendered bytes. It was recomposed on every
  request — measured at 1.9 ms and 573 KiB of allocation per request for a five-endpoint
  application — on a route that needs no authorization, which made it an amplification anyone could
  reach. The same measurement after the change is 0.07 ms and 61 KiB, which is the cost of writing
  the response. Endpoints are fixed once the host starts, so the rendering is reused for the life of
  the process; it happens on first use rather than during startup, keeping the cost off the critical
  path of a host that never serves the document. A document that cannot be composed still fails
  every request. Media types and the 404 for an unknown document name are unchanged.
- **Telemetry:** every Portia duration histogram now advises explicit bucket boundaries. Without
  advice a collector applies its own defaults, which run from 5 to 10,000 and suit milliseconds;
  these instruments record seconds, so all ordinary measurements fell into the first bucket and no
  percentile was recoverable. Latency instruments use the seconds-valued boundaries OpenTelemetry's
  semantic conventions recommend, and `portia.processor.lag` uses a wider backlog scale. Existing
  dashboards built on the previous (unusable) distribution will change.
- Aggregates no longer retain committed history. An aggregate replays its whole stream and may be
  caught up in place for the life of the process, so holding every event it had seen grew the
  instance without bound; `Version` and `CommittedStreamPosition` already carry what the framework
  needs. `AggregateScenario.CommittedEvents` is replaced by `AggregateScenario.CommittedEventCount`.
- Dispatching a result-bearing request no longer allocates a closure per call to look up its cached
  pipeline plan, and Fitz envelopes are written through a buffer writer instead of being built and
  then copied. A request result is written straight into its envelope rather than through an
  intermediate `JsonElement` copy.
- `JsonRequestSerializer` rejects one request type registered under two discriminators instead of
  silently keeping whichever arrived last.

- Track the nullability-aware public API of every shipped assembly during ordinary builds, and add
  real-Fitz distributed workload-coordinator conformance coverage for exclusive ownership, stable
  reconciliation, revocation, and shutdown. Publishing now requires Unshipped APIs to be promoted.
- Make domain-event catalog incremental cache models structurally comparable without retaining
  Roslyn locations, while preserving diagnostic spans and partial-declaration deduplication.
- Make manual publishing rebuild, run the packed ASP.NET Core consumer, and pass the complete
  Compose-backed suite for the exact release checkout before any package is pushed. CI now uploads
  its Cobertura coverage output for inspection.
- Bound projector and reactor passes to 4,096 events by default, while immediately continuing
  progressing durable backlogs in fresh dependency-injection scopes.
- Make `portia.worker.failure` use the stable `runner` and `stage` dimensions; Fitz partition-stop
  timeouts now use structured log event ID 1101.
- Treat clean early Fitz read disposal as success and keep cross-record event-ID uniqueness at the
  aggregate hydration boundary instead of retaining full-stream reader state.
- Cache rendezvous owners across unchanged fleet reconciliation and read coordinator workload
  snapshots directly from the concurrent active registry so tenant churn cannot publish stale work.

### Added

- `PortiaFitzBuilder.UseKvCheckpoints(route)` registers `FitzKvCheckpointStore` as the application's
  durable `IProjectionCheckpointStore`, and `AddFitz` now publishes the shared connection's
  `IKvClient` so a repository deriving from `FitzKvProjectionStore` takes it as an ordinary
  constructor dependency instead of opening a second connection to the same broker. Reactor
  checkpoints remain an explicit selection rather than a default, and a second call naming a
  different route is rejected. See [projectors and reactors](docs/projectors-and-reactors.md).

- Queue terminal callbacks now receive `QueuedRequestTerminalReason`, distinguishing retry-limit,
  permanent-result, and actor-validation outcomes. `TerminalHandlerMissingException` faults a
  hosted runner before transport disposition when a terminal callback is unavailable.

- `IResumableTenantDirectory` and `ITenantDirectoryCursor` provide independent, in-process cursor
  progress across watch reconnects. `MultiTenantRunnerOptions` configures restart, shutdown, and
  per-tenant stop deadlines; `TenantStopTimeoutException` identifies cleanup that exceeds its
  shared deadline. The existing `MultiTenantRunner` constructor remains unchanged.

- Benchmarks now cover asynchronously yielding request behaviors, 10,000- and 100,000-event
  aggregate histories, and cold and warm HTTP member binding. The maintained performance and
  scaling guide records the machine, commands, results, supported capabilities, and operational
  boundaries.

### Changed

- Permanent handler results and actor-validation failures now take the queue terminal callback
  path regardless of the configured retry threshold. Retryable errors and unexpected exceptions
  still abandon below the threshold. Every terminal callback completes before the single
  acknowledgment, and both terminal setup and callback failures cross generic and Fitz hosted
  worker restart boundaries.

- Request pipeline continuations are single-use during their behavior invocation. Skipping one is
  valid; a second, concurrent losing, or retained late invocation throws `InvalidOperationException`.
  Dispatch shares one typed context across matching policies, behaviors, and the handler and uses
  cached shape-specific pipeline plans. Five-behavior allocation fell from 992 B to 216 B in the
  maintained same-session benchmark.

- `MultiTenantRunner` prefers a resumable directory cursor when available and bounds ordinary
  removal with one deadline shared by workload cancellation and the stop callback. Legacy tenant
  directories keep complete-snapshot plus watch behavior, and host shutdown retains its shared
  grace period.

- Hosted startup now rejects selected `[RequiresPermission]` handlers when the composed service
  graph has no `IPermissionEvaluator`, listing guarded request CLR types in stable order without
  instantiating a scoped evaluator. Direct non-host composition is unchanged.

- `PORTIA100` now recognizes known effect dependencies through canonical type, base-type, and
  interface metadata names. `PORTIA101` recognizes service-provider/scope dependencies and
  semantic `ActivatorUtilities` calls. Both warnings explicitly remain best-effort architecture
  heuristics rather than proofs of arbitrary application behavior.

- Aggregate hydration passes its populated event list directly into a span-backed validation and
  replay path, removing a redundant reference array while retaining whole-batch validation before
  application. HTTP binding weakly caches immutable member metadata and only the derived JSON
  options needed for property converter or number-handling overrides, isolated by application
  options identity.

- Hosted component workloads retain a healthy event notification subscription across successful
  passes while still creating a fresh dependency-injection scope for each pass. Notification
  failure recreates the subscription; a processor failure does not. A pattern that changes across
  scopes now faults as an invariant violation.

- Unary request dispatch with no pipeline behaviors skips construction of an unused delegate chain.
  Aggregate hydration validates event IDs in the aggregate's existing set instead of allocating a
  second one, and the repository no longer repeats identity, version, and audit validation already
  performed atomically by `Aggregate`. Runtime contracts and validation guarantees are unchanged.

- Analyzer guidance now distinguishes a batch handler on the wrong processor base (`PORTIA017`)
  from selecting both single and batch handling for one event (`PORTIA028`), and duplicate
  domain-event discriminator errors (`PORTIA023`) name both CLR types at the duplicate declaration.
  Architecture diagnostics now ship in the pure `Cntryl.Portia.Analyzers` package, separate from the
  workspace-dependent `Cntryl.Portia.CodeFixes` package. Its fixes for `PORTIA002` and `PORTIA005` add
  the missing `partial` modifier and support Fix All.
  The getting-started guide now covers GitHub Packages authentication, core concepts, and the
  compile-time versus startup-validation boundary.

- Component base types dropped their `Base` prefix: `Projector`, `Reactor`, `BatchProjector`,
  `BatchReactor`. `Aggregate` never carried one and these are the same kind of thing.

- `IReactorContext<TEvent>.Ev` is now `.Trigger`, and `DomainEventRecord.Ev` is now `.Event`. `Ev`
  was an abbreviation on the property reactor code touches most, against `IRequestContext`'s
  spelled-out `Request`. The interface member cannot be called `Event` — CA1716 reserves it on a
  virtual or interface member — so the typed one is named for its role instead.

- The pipeline continuation delegates are `RequestPipelineNext`, `RequestPipelineNext<TOut>`, and
  `StreamRequestPipelineNext<TOut>`, with the parameter named `continuation`. They are the rest of
  the pipeline, not siblings of `IRequestHandler` and `IStreamRequestHandler`.

- `IRequestAuthorizer<TRequest>.AuthorizeAsync` lost its `actor` parameter: the actor is
  `context.Actor`, and passing it twice gave one call two sources of truth for who is acting. `ct`
  also lost its default, matching `IRequestHandler`.

- `Cntryl.Portia.Testing` lives in the `Cntryl.Portia.Testing` namespace, along with `Cntryl.Portia.Fitz`'s
  `InMemoryRpcClient` and `InMemoryLeaseClient`. Test doubles and conformance suites no longer sit
  in an application's completion list beside the production contracts.

- `RequestDispatch.SendAsync` takes a `RequestDelivery` rather than six positional arguments, two
  of them adjacent nullables. `DeserializedRequest` carries the wire `Name` its envelope declared,
  and an adapter builds one value with `envelope.ToDelivery(invocation, clock)`. `IQueuedRequest`
  and `RequestNotification` gained an optional `Name` for adapters that resolve a discriminator.

- `PortiaTelemetry.RecordRunnerFault` takes a `RunnerFaultStage` rather than a reason string. The
  string was never logged — it only selected a stage, by substring match over its wording — so
  callers were building messages, some interpolating a tenant id, that were then discarded.

- `PortiaHttpPayloadTooLargeException` is `HttpPayloadTooLargeException`, matching
  `EventStreamConcurrencyException` and every other exception in the framework.

- `Result` and `Result<T>` annotate `IsSuccess` so `result.Error` in a failure branch needs no `!`.
  `Result<T>.Value` now returns `T`, not `T?`, so a non-nullable type argument stays non-nullable to
  consumers; `Result<string?>` still exposes `string?` and may legitimately succeed with null.
  Failed and uninitialized access keep their existing exceptions.

- `Uuid` implements `ISpanParsable<Uuid>`, `ISpanFormattable`, and `IComparable<Uuid>`, delegating
  parsing, standard formatting, and ordering to its wrapped `Guid`. Parameterless formatting,
  equality, JSON representation, and UUID generation semantics are unchanged.

- `Cntryl.Portia.AspNetCore` now depends directly on `Cntryl.Portia.DependencyInjection`. A consumer referencing
  only the HTTP package receives the registration APIs, generator/analyzer assets, and interceptor
  compiler configuration transitively.

- HTTP errors advertised as `application/problem+json` now contain RFC 9457 problem details
  (`type`, `title`, `status`, `detail`, and `instance`) instead of a Portia-only `message` object.
  Request errors additionally expose their retry classification through the `transient` problem
  extension and the `Portia-Transient` header. Bodyless 401 responses now send
  `WWW-Authenticate: Bearer` without exposing authorization failure details.

- Projection-store optimistic conflicts now derive from the public, adapter-neutral
  `ProjectionConcurrencyException`, and `ProjectionStoreConformance` enforces that contract.
  `FitzKvConcurrencyException` remains as an obsolete compatibility subtype, while current Fitz
  stores throw the shared type directly.

- `TerminalHandlerFailureException` is public, so applications can distinguish a queue terminal
  callback failure that faults `QueueRunner` and deliberately leaves its delivery unacknowledged.
  Both the standalone hosted runner and Fitz application worker propagate this terminal fault
  instead of treating it as a reconnectable transport-stream failure.

- `AddPortia()` now registers serialization startup validation even when `AddWorkers()` is not
  called. API-only hosts therefore validate the resolved JSON options, upcaster identities,
  duplicates, and transitions before accepting requests.

- Hosted projector and reactor failures now retry with exponential backoff from `PollInterval`,
  capped by `WorkloadOptions.MaximumFailureDelay` (one minute by default). After
  `FailureAttemptLimit` consecutive failures (ten by default), the worker faults with public
  `WorkloadFailureException` instead of silently rereading one poison event forever. A successful
  pass resets the failure count and delay; checkpoints still advance only through successful
  commits. Duplicate workload declarations compare these policy values and reject conflicts
  rather than silently retaining the first declaration.

- `EventStreamPattern.ForPattern` now rejects a resource when its area is absent instead of
  silently treating `stream://realm/*/resource` as a realm-scoped checkpoint.

- `MultiTenantRunner` passes the run cancellation token to tenant-stop callbacks during shutdown
  and bounds the wait with that token. Ordinary tenant removal still uses a non-cancelled token so
  application cleanup can finish.

- `IExecutionContext.Actor` still returns an independent copy per read — an authorizer's mutation
  must not reach the handler — but Portia no longer takes that copy for its own internal null
  checks and system-identity tests. A dispatch with three authorizers took five deep copies of the
  principal; it now takes only the ones application code asks for.

- Authorizers are split by stage once at registration rather than re-filtered per dispatch, and
  each stage name is rendered once rather than through `ToString().ToLowerInvariant()` per call.

- `IProjectorHandler`, `IBatchProjectorHandler`, `IReactorHandler`, `IBatchReactorHandler`, and
  `IReactorContext<TEvent>` constrain `TEvent` to `DomainEvent`, matching `Aggregate.On<TEvent>`;
  `IReactorHandler<string>` used to compile. The two batch interfaces are contravariant like their
  single-event counterparts.

- `RequestBus`'s two unary dispatch paths are written out rather than unified through four
  delegates. The streaming path could never use that unifier — a `yield return` cannot sit inside a
  `try` with a `catch` — so it covered two of three paths at the cost of a five-argument delegate
  at each, and the third duplicated the sequence anyway.

### Telemetry

- `request.type` carries each request's declared discriminator rather than its CLR type name:
  `greetings.create`, not `CreateGreeting`. A request with no discriminator — one never transported
  — still reports its type name. This is what the discriminator is for; the framework makes it a
  compile error to omit one on a transported request, then named spans after the class anyway, so
  renaming a class silently re-keyed every dashboard.

- The `component` tag on `portia.authorization.duration` names the authorizer. It reported
  `RequestAuthorizerRegistration\`2` — the generic registration wrapper — identically for every
  authorizer, so measurements from different policies could not be told apart.

- A tenant workload that faulted reported the `cleanup` stage, because its diagnostic text
  happened to contain the word "callback"; fleet membership faults reported `execution` for the
  same reason. Both now report the stage the calling code names.

- The units on `portia.workload.active`, `portia.worker.failure`, and `portia.worker.restart` are
  corrected from `{request}` to `{workload}`, `{failure}`, and `{restart}` respectively. Instrument
  names, dimensions, and telemetry version remain unchanged.

### Fixed

- `ConfigureWorker` rejected its own registrations. It compared the two callbacks to decide whether
  a repeat declaration conflicted, but a lambda that captures anything allocates a fresh delegate
  per call and never compares equal, so running shared application setup twice — the documented
  API-host-and-worker-host pattern — threw. The name is now the identity: the first declaration
  under a name wins, like every other builder method.

- An uninitialized `Result` returned from an authorizer named
  `Cntryl.Portia.RequestAuthorizerRegistration\`2` rather than the offending authorizer, defeating
  the point of naming it.

- `CreateEffectId` derived a persisted deduplication key from `WorkloadIdentity`'s generated
  `ToString`, so adding a property to that record could silently re-key every future effect. The
  explicit formatter now freezes the exact legacy field order and text. Every existing persisted
  effect UUID remains unchanged; this introduces neither new keys nor a migration.

- `Aggregate` reported "Concurrent aggregate emission, replay, or save is not supported" for a
  re-entrant emit — an `On<TEvent>` handler raising while applying — sending readers looking for a
  second thread that was never there. The message names both causes.

- `Aggregate` rescanned both pending-event lists on every raise to check event-ID uniqueness,
  making a command that raises n events cost O(n squared) for a question one hash set answers.

- `Aggregate`'s audit session stream used `Guid.NewGuid()` directly, bypassing `Uuid` and the
  injectable metadata factory, so it was the one aggregate identity a test could not control.

- A fired durable schedule delivered with a trusted system actor reported `fitz.schedule` while
  every other path reported the invocation's own name, so the transport label depended on which
  authentication path the delivery took. Both paths now read it from the invocation.
- `PortiaWorkloadService` stopped passing an `ILogger<MultiTenantRunner>` when it moved to
  explicit constructor injection, silencing tenant-directory and tenant-start faults for any host
  without an `ActivitySource` listener. The logger is injected and passed through again.
- A pipeline behavior selected by scope but unable to serve the dispatched request's shape — a
  no-result behavior reached through a result-bearing dispatch of a request implementing both —
  threw `InvalidCastException` from inside the bus. Such a behavior is now skipped.
- `PORTIA015` did not cover pipeline behaviors, so a behavior declared in an unsupported shape got
  no diagnostic. Every generator now reads one shared list of component roles.

### Added

- `EventStoreConformance` in `Cntryl.Portia.Testing`: append ordering, contiguous resource offsets,
  offset resumption, pattern-read coverage, and the requirement that a stale append throw
  `EventStreamConcurrencyException` and write nothing.
- Practice diagnostics, all warnings: `PORTIA100` (a projector must not take a dependency that can
  cause an effect), `PORTIA101` (a component must not resolve services from the container),
  `PORTIA102` (an aggregate must not depend on a service), `PORTIA103` (one type, one request
  handler), `PORTIA104` (an unexpected failure stays an exception rather than becoming a failed
  `Result`).

### Changed

- `RequestDeliveryScopes.Fixed` and `FixedQueue` return a new scope per call, matching the port's
  "one independently disposable scope per delivery" contract instead of relying on disposal being
  a no-op.
- Applicable authorizers and behaviors are computed once per request type and cached, rather than
  re-filtered and re-allocated on every dispatch.
- Workload hosting asks a descriptor whether it supports rebuild generations instead of testing
  its type, and Fitz worker definitions build their own runners instead of being switched on.
## 0.2.0

- Replaced projection scope offsets with backend-owned `EventCursor` values and structural checkpoint patterns.
- Required stable, explicit IDs when registering projector and reactor workloads.
- Replaced the closed request-transport enum with stable, extensible transport IDs discovered from
  annotated marker interfaces, while preserving the built-in callable, queue, notice, and schedule
  capabilities.
- Made HTTP/OpenAPI activation explicit through `AddHttp()` and `MapPortiaOpenApi()`, removed
  ambient `WebApplication` interception, stopped replacing ASP.NET Core's global JSON options, and
  moved HTTP binding generation into the ASP.NET Core adapter package.
- Fitz queue consumers reserve one item at a time, validate durable-attempt support inside
  `QueueRunner`, and reject undeclared inbound transport capabilities before dispatch.
- Projector and reactor pass limits now complete an already-started atomic batch before yielding.
- Fitz projection batches now reject stale checkpoints inside the read/write transaction.
- Fitz checkpoints now use a versioned UTF-8 representation while continuing to read the 0.1.x
  eight-byte unsigned big-endian offset format.

## 0.1.0

- Initial public release of the request pipeline, event sourcing, projection/reactor runtime, Fitz,
  HTTP, telemetry, JWT, testing, analyzer, and generator packages.

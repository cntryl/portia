# Changelog

Notable changes to Portia. Entries call out anything that changes observable behavior for an
application already running on a previous version, including telemetry, since dashboards and
alerts are as breaking to change as an API.

## Unreleased

### Added

- `PortiaHttpOptions.ServeOpenApi` controls whether Portia maps `/openapi/v1.json` and
  `/openapi/v1.yml`, defaulting to `true`. Portia previously mapped them by intercepting `Build()`
  with no way to opt out, which took a deployment decision — whether a schema is publicly reachable
  — away from the application. Setting it to `false` withdraws Portia's routes and leaves the
  document registered, so the application can map it on another path, behind authorization, or on a
  separate port and receive the same composed document.
- The package smoke consumers map the local package folder in their own `packageSourceMapping`.
  The repository root maps every package to nuget.org and a nearer mapping replaces rather than
  extends it, so a cold restore could not see the freshly packed smoke packages and resolved a stale
  version or failed outright; the step only passed against a warm global package cache.
- Every package declares `PackageTags`.
- Portia is licensed under the Apache License, Version 2.0. Every packable project declares the
  `Apache-2.0` SPDX expression, so a consumer's license scanner resolves the terms from the package
  itself, and packing now fails if a packable project declares no license at all.

### Fixed

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

### Changed

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
  Architecture diagnostics now live in the pure `Portia.Analyzers` project, separate from the
  workspace-dependent `Portia.CodeFixes` project. Its fixes for `PORTIA002` and `PORTIA005` add
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

- `Portia.Testing` lives in the `Cntryl.Portia.Testing` namespace, along with `Portia.Fitz`'s
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

- `Portia.AspNetCore` now depends directly on `Portia.DependencyInjection`. A consumer referencing
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

- `EventStoreConformance` in `Portia.Testing`: append ordering, contiguous resource offsets,
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

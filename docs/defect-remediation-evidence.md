# Framework defect remediation evidence

This is a breaking API correction. Publication and tagging are outside this PR.
The event stream remains the source of truth. Fitz owns reservation redelivery and
dead-letter policy; this work adds no outbox or application redelivery loop.

## Corrected audit and OCC contract

The user's correction supersedes the supplied plan's shared-source-stream rules:

- Raised events append to the aggregate's stable stream using its committed OCC position.
- Each audit batch appends to a new session stream with a UUIDv4 resource in the
  aggregate's realm and area, starting at physical offset zero.
- Audits retain aggregate identity/state version in metadata, and never advance
  the aggregate's state or expected event-stream append position.
- A failed audit save keeps its pending payloads and allocated session address;
  successful save clears both. A later batch receives a new UUIDv4 session stream.
- Aggregate reload reads only the raised-event stream. An audit before creation
  leaves that stream absent. Area/realm consumers can read both kinds across streams.
- Exclusive pending kinds ensure each save targets exactly one stream/session.

Each row records an observed regression before its production correction. Pending
rows are acceptance criteria, not claims of completion. Consumer tests have no
`InternalsVisibleTo` access. Compiler regressions must assert successful compilation
and execution (except explicit diagnostic tests for unsupported inputs).

| Slice | Regression / observed red | Green evidence | Production entry point |
| --- | --- | --- | --- |
| Consumer infrastructure | Baseline established before introducing defects | `ConsumerBaselineTests`: 2 passed; separate contracts and feature assemblies; compile/execute harness | Public `Aggregate`; generator consumer compilation |
| Exclusive pending kind | Both emission orders failed with `Assert.Throws: No exception was thrown` | Both now pass, including unchanged metadata calls/state/version and subsequent same-kind emission; internal history test uses separate saves | `Aggregate.RaiseEvent`, `Aggregate.AuditEvent`, repository save |
| Aggregate persistence | Public API initially missing; wrong mixed-stream position, audit-before-creation and concurrent-operation regressions observed | 27 consumer tests pass, including UUIDv4 sessions, OCC, long audit pages, stored metadata compatibility and append/commit cleanup | Repository, factories, both stores, serializer, testing helpers |
| Dispatch and modules | Observed circular dependency, unrelated construction, missing/duplicate interfaces, invalid shapes and missing module contributions | Compile-and-execute regressions pass; module order, conflicts, idempotence and authorization covered | Shared dispatcher and generated module descriptors |
| Hosting and lifetimes | Observed one reactor instead of two; missing generated projector resolution; root/scoped resolution failures for components and all three inbound transports | Public scope/multiplicity and recovery tests pass; format, Release build, Compose suite (196 + 54) green | DI hosting, transports, projector/reactor runners |
| HTTP contracts | Nullable compilation, constant route fallback, ignored defaults/options, invalid roots, missing diagnostics, streaming authorization/disposal, missing async route resolver and optional token failures observed | 32 public consumer HTTP tests pass; format, Release build and Compose suite (196 + 86) green | HTTP generator and endpoint mapping |
| Queue ownership | Malformed JSON ended enumeration; defaults were 5,000 seconds/16 items; no lease renewal; pending acknowledgment stopped renewal; hosted consumers terminated on faults/EOF | 16 public tests pass, including real broker handoff; format, Release build and Compose suite (196 + 102) green | Queue runner, Fitz consumer and hosted lifecycle |
| Tenant lifecycle | Pending: idle polling; restart/backoff; removal; duplicates; independent watchers; shutdown | Pending | Tenant directory and runner |
| Complete workflow | Pending: two modules, persistence, two reactors/projectors, direct/HTTP/RPC/queue, declined audit, state/stream/projections | Pending | Consumer host fixture and onboarding |
| Final readiness | Pending: full format/build/Compose tests; complete review; rebase; final SHA hosted CI; squash merge and main readback | Pending | Single coordinated PR |

## Commands

- Targeted: `dotnet test test/Portia.ConsumerTests/Portia.ConsumerTests.csproj -c Release --filter <regression>`.
- Format: `dotnet format Portia.slnx --verify-no-changes`.
- Build: `dotnet build Portia.slnx --configuration Release`.
- Integration: `docker compose up -d fitz`, then `dotnet test Portia.slnx --configuration Release --no-build`; stop with `docker compose down --volumes`.

## Baseline

- Refreshed `main`: `0ab2048`; clean checkout; no existing remediation PR.
- Existing aggregate tests: 16 passed before changing the mixed-save contract.
- New consumer baseline: 2 passed before introducing emission regressions.

## Observed TDD cycles

- Public persistence registration: consumer compile-and-execute assertion failed with
  CS1061 for missing `AddPortiaAggregate`; after adding the factory and scoped
  repository, create/save/load returned the persisted balance (12).
- Original shared-stream audit scenarios: four failures across memory and Fitz,
  reporting aggregate version 0 versus physical position 1, or version 1 versus
  physical position 2. These passed after separating state version and stream
  position, but **were superseded by the user's separate-route/OCC correction**.
- Public testing/serializer API: consumer compilation failed for inaccessible
  `AttachMetadata` and missing `AggregateScenario`/`DomainEventSeed`. The test now
  compiles and executes, including rejection of a second metadata attachment.
- Concurrent emission/save: all three controlled tests (event, audit, save) failed
  with no exception thrown while an append was pending. All now pass with an
  aggregate operation guard. Append failure preservation and empty-save behavior
  were already green when their tests were introduced; no red is claimed for them.
- Latest route-independent gate: 6 passed (emission exclusivity, public testing
  helpers, concurrent operation rejection).

- Corrected audit-session regressions: six failures showed audit writes advancing
  event-stream positions and audit-before-creation returning an aggregate. These
  pass after routing audit batches to independent UUIDv4 session resources.
- The real-broker area read then failed on a missing realm offset. Scope offsets
  now remain nullable when absent; checkpointing requires the selected scope's
  offset. The corrected persistence gate passes: 17 consumer and 30 internal tests.
- Fitz session disposal: four tests failed because sessions were not disposed on
  success, append failure, or commit failure. All four now pass; rollback/disposal
  failures do not replace the original append/commit exception.
- Persistence boundary: format verification and Release build (zero warnings/errors)
  passed. All 196 existing tests and 27 consumer persistence/baseline tests passed.
  Two newly introduced dispatch tests failed as intended: circular bus/handler DI
  dependency and construction of an unrelated handler. Those are the next slice.
- Additional persistence coverage passes for 1,025-record audit sessions, concrete
  offsets across audit-only pages, missing stored `is_audit` defaulting to false,
  aggregate-owned classification, and stable session identity across failed saves.

## Dispatch and module composition

- Nested dispatch and selected construction: observed DI circular dependency and
  unrelated-handler constructor exception; both now pass with shared scoped
  `RequestBus`, typed descriptors, and on-demand dependency resolution.
- Multi-interface/partial discovery: observed missing second handler and false
  `PORTIA008` duplicate handler; both now pass. Multi-interface authorizers are also
  covered, and unselected authorizers are not constructed.
- Explicit module API: consumer compilation initially failed for missing
  `AddPortiaModule`; named `[PortiaModule]` partial types now compose idempotently.
- Contract transports: both module registration orders initially produced zero
  registrations. Explicit imported `ContractsModule` supplies contracts once; both
  feature modules contribute events/handlers, and the shared catalog sees all three.
- Global/nested/partial component compilation: observed invalid namespace emission,
  missing overrides and repeated-source failures; all three cases now compile and
  execute projector and reactor dispatch. Source identities include a full-symbol
  SHA-256 suffix. Unsupported shapes receive `PORTIA014`/`PORTIA015` diagnostics.
- Additional checks pass for conflicting handler/authorizer modules with registration
  rollback, and two handlers sharing a simple name in different namespaces.
- Existing test bus construction now uses an owned DI scope and public module
  registration; it no longer manually constructs every generated bus dependency.
- Boundary: Release build and format verification passed; Compose tests passed
  196 existing + 41 consumer tests, then the added name-collision test passed.
  An earlier full run failed because Docker had stopped Fitz (broker logged SIGTERM);
  Compose restart and the full rerun resolved the environmental failure.

## Hosting and dependency lifetimes

- Four component regressions failed: one reactor worker instead of two, scoped
  reactor resolved from root, and missing `Projector<ConcreteProjector>` service for
  both projector cases. Concrete component hosting now consumes module descriptors
  and owns an async scope around each existing runner pass.
- Queue and notification scope validation both failed on singleton runners capturing
  the scoped bus. Both now pass, including fresh delivery scopes, nested dispatch
  sharing the current scope, and disposal on success, failure and cancellation.
- RPC scope validation failed on singleton server capturing scoped bus. The server
  now takes a scope factory and resolves serializers, validator and bus per callback.
  Multiple calls and handler failure dispose their scopes; no application dependency
  is captured by a long-lived RPC registration.
- Two clock-driven fresh-pass tests pass. Additional public tests cover interrupted
  passes and a durable projection commit followed by a failed response and failed
  checkpoint reload; authoritative progress must be reloaded before execution.

- Hosting boundary: format verification, Release build (zero warnings/errors), and
  Compose tests passed (196 existing + 54 consumer). Fixture transport count was
  updated to include its new scope-test request while retaining both feature checks.

## HTTP binding and streaming

- Passing consumer HTTP baseline preceded regressions. Ten failures reproduced
  nullable scalar compile errors, literal/constant binding differences, missing
  defaults, ignored JSON names/converters and invalid-root exceptions. All fifteen
  baseline/binding cases now compile and execute with configured ASP.NET JSON options.
- Two unsupported-shape tests failed to find any diagnostic. They now report
  `PORTIA016` with constant-route or scalar-binding guidance. Invariant decimal
  parsing and semantic exclusion of unrelated mapping methods also pass.
- Streaming regressions produced four unhandled authorization failures and three
  disposal timeouts. All ten JSON/SSE cases now pass: authorization before response
  start, one enumeration, disposal on success/cancellation/failure, and abort after
  an error once streaming has begun.
- Async-route consumer compilation failed for missing `WithPortiaRouteValues` and
  generated code relying on an implicit LINQ import. Both are fixed; endpoint realm,
  resource, bearer token and payload reach the queue publisher correctly.
- Optional route regression returned null for `/binding/7` with `{id?}`. Optional,
  constrained, default and catch-all token prefixes are now recognized; the nullable
  optional-route cases pass. HTTP targeted gate: 32 consumer tests passed.
- Existing HTTP tests configure snake case explicitly; new consumer tests verify
  both the ASP.NET camel-case default and explicitly configured snake case.

- HTTP boundary: format verification, Release build (zero warnings/errors), and
  the Compose-backed suite passed (196 existing + 86 consumer tests).

## Fitz queue ownership and consumer recovery

- Two regressions failed: malformed JSON escaped reservation enumeration, and the
  default reserve tuple was `(5000 seconds, 16 items)` instead of `(5, 1)`.
  Deserialization now occurs inside each delivery boundary while its reservation
  handle remains available. Failed/malformed deliveries are neither acknowledged
  nor republished; successful and intentionally terminal outcomes retain acknowledgment.
- Active-renewal regression timed out before any extension. It passed after Fitz
  `ExtendAsync` renewal was added, then was refactored to deterministic clock control.
  A follow-up regression caught renewal stopping during a pending acknowledgment;
  renewal now continues until acknowledgment finishes. Abandonment, cancellation
  and disposal stop renewal. Renewal loss cancels the active delivery and blocks ACK.
- Four hosted recovery regressions failed on transport exceptions or clean EOF.
  Queue and notification hosting now retry enumeration after a one-second delay
  using `TimeProvider`. Eight clock-driven cases cover restart and shutdown during
  both active execution and backoff. This loop never republishes a message.
- Two real-broker tests prove valid acknowledgment, malformed reservation expiration,
  retention through a three-second competing reserve with a one-second lease, and
  redelivery after processing cancellation. Targeted queue gate: 16 tests passed.
- The real-broker gate initially assumed an increasing SDK attempt value and failed.
  The pinned Fitz .NET 0.1.1 source at commit
  `ece2017e5b49eea08d52f731586bf81ae0dfeebe`,
  `src/Core/Domains/Queue/QueueClient.cs`, constructs reserve results with attempt 1;
  the wire result does not expose its durable broker counter. Tests preserve that
  supplied value; separate controlled reservations prove values 6 and 9 are forwarded
  unchanged. Portia does not infer or maintain a delivery counter.
- Compose does not configure a dead-letter threshold. These tests prove redelivery
  handoff, not a live DLQ threshold transition. DLQ activation remains Fitz configuration.

- Queue boundary: format verification, Release build (zero warnings/errors), and
  Compose tests passed (196 existing + 102 consumer).

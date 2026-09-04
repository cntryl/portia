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
| Aggregate persistence | Public API initially missing; wrong mixed-stream position, audit-before-creation and concurrent-operation regressions observed | Corrected UUIDv4 sessions and OCC pass in both stores; remaining checks: long audit pages, metadata compatibility, append/commit cleanup | Repository, factories, both stores, serializer, testing helpers |
| Dispatch and modules | Pending: nested bus; selected dependencies; two modules/order/idempotence/conflicts; names/interfaces; partial/global/nested components; catalogs/transports; authorization order | Pending | Shared dispatcher and generated module descriptors |
| Hosting and lifetimes | Pending: component multiplicity; generated projector hosting; delivery/pass scopes and disposal; uncertain checkpoint commit | Pending | DI hosting, transports, projector/reactor runners |
| HTTP contracts | Pending: nullable/default/required/null; constants; configured JSON names/converters; invalid kinds; invariant scalar binding; diagnostics; interception identity; streaming authorization/disposal; async route values | Pending | HTTP generator and endpoint mapping |
| Queue ownership | Pending: malformed/failed delivery; unchanged reservations; recovery/backoff/cancellation; acknowledgments; seconds; renewal; real broker redelivery handoff | Pending | Queue runner, Fitz consumer and hosted lifecycle |
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

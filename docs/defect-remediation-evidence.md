# Framework defect remediation evidence

This is a breaking API correction. Publication and tagging are outside this PR.
The event stream remains the source of truth. Fitz owns reservation redelivery and
dead-letter policy; this work adds no outbox or application redelivery loop.

Each row records an observed regression before its production correction. Pending
rows are acceptance criteria, not claims of completion. Consumer tests have no
`InternalsVisibleTo` access. Compiler regressions must assert successful compilation
and execution (except explicit diagnostic tests for unsupported inputs).

| Slice | Regression / observed red | Green evidence | Production entry point |
| --- | --- | --- | --- |
| Consumer infrastructure | Baseline established before introducing defects | `ConsumerBaselineTests`: 2 passed; separate contracts and feature assemblies; compile/execute harness | Public `Aggregate`; generator consumer compilation |
| Exclusive pending kind | Pending | Pending | `Aggregate.RaiseEvent`, `Aggregate.AuditEvent`, repository save |
| Aggregate persistence | Pending: public create/save/load; events/audits/events; audit-only streams/pages; physical conflicts; append/commit failure; empty save; metadata compatibility; concurrent save/emission | Pending | Repository, factories, both stores, serializer, testing helpers |
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

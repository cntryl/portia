# Execution-context verification

Verified locally on 2026-09-06 at the execution-context milestone, before the
[explicit registration refactor](workload-registration-verification.md). No packages were
published and no production environment was deployed.

## Red → green evidence

- Envelope contract tests first failed because serialization could not carry request
  metadata and deserialization could not expose it. Version 1 envelopes now require
  logical metadata and reject missing identities and unsupported versions.
- Failed-save regressions demonstrated that both raised-event and audit batches
  could accept additional emissions after an unconfirmed append. Both tests failed
  with “No exception was thrown.” Pending batches now reject additional emissions
  until the original save succeeds, retaining attribution across retries.
- Public consumer coverage exercises incremental hydration, whole-batch attribution
  validation, cancellation, optimistic concurrency, ambiguous saves, explicit parent
  dispatch, streaming authorization, and independent system reaction principals.
- Transport tests cover HTTP facts, RPC child propagation, real queue redelivery,
  notice routes, and independent logical identities for recurring schedule firings.
  Notice and schedule adapter tests use deterministic implementations of the Fitz
  public client interfaces. RPC and queue tests also run against the real broker.

## Final checks

```sh
dotnet restore Portia.slnx --locked-mode
dotnet build Portia.slnx --configuration Release --no-restore
dotnet format Portia.slnx --no-restore --verify-no-changes
FITZ_TEST_ENDPOINT=ws://127.0.0.1:14090/ws dotnet test Portia.slnx \
  --configuration Release --no-build --no-restore
```

The Release build completed with zero warnings and errors. Formatting and locked
restore passed. All **392 tests** passed: 202 framework tests and 190 consumer tests.
The tests used an isolated Compose project on port 14090 with the image pinned in
`compose.yaml`; other broker containers were left untouched.

## Installed-package proof

Local packages were packed as `1.0.0-context-20260906` and consumed outside the
repository by a shared application project, a separate ASP.NET API executable,
and a separate .NET worker executable. These projects had no Portia source project
references or repository build settings. The generator package supplied its own
interceptor configuration.

Both direct HTTP and HTTP `Prefer: respond-async` completed this path:

```text
HTTP → command → aggregate event → system reaction → receipt event → projection
HTTP 202 → Fitz queue → command → aggregate event → system reaction → receipt event → projection
```

The API accepted both requests while the worker was stopped, without activating
queue or component workers. Starting the worker produced two projected receipts
with amounts 8 and 13. Assertions verified source actors (`alice` and `proof:worker`),
reaction actor `proof:reactor`, causation back to the triggering event, inherited
correlation, fresh execution identities, UUID v4 event/execution identities, and a
UUID v5 receipt aggregate identity.

The proof used one worker, an application-owned file projection target, and an
in-memory reactor checkpoint store. It validates installed API composition and the
context chain, not production storage fencing, crash durability, or autoscaling.
Application idempotency and broker-specific delivery limits remain explicit in the
[context guide](request-context.md).

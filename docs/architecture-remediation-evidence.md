# Architecture remediation evidence — 2026-09-06

Changes were prepared on baseline `7feeab16b7087677105b7d23770c1b837f804f69`.
The six Portia changes are implemented, but acceptance is **blocked** by missing structured
APPEND error codes in the centrally pinned Fitz 0.1.1 client. No Fitz repository change,
compatibility adapter, message-text fallback, publication, or deployment was made.

## Observed red → green cycles

The new identity and fleet APIs first received compiling contracts while retaining the
old name-only loading or compete-for-every-partition behavior. Compile/analyzer errors
encountered while writing tests were corrected before the recorded behavioral red runs.

| Finding | Observed behavioral red | Focused green |
| --- | --- | --- |
| Checkpoint scope | `ReactorsResumeIndependentlyAcrossTenantsAndPatterns`: expected offset `1`, actual `3` after writing three same-name identities | Scoped dictionary keys and reactor loading/saving preserve independent progress; initial checkpoint/hosting gate: 11 passed |
| Rebuild identity | `RebuildGenerationsResumeAndLeaveLiveDataUntouched`: the `first` generation was absent because loading live progress skipped its first event | Generated registrations and batch contexts use the same complete identity. First/second rebuilds, resume, and untouched live data/progress pass |
| Fleet redistribution | Healthy worker retained all eight assignments after `b` joined; deadline expired waiting for four. Unready snapshot also left work running. Six invalid fleet registrations threw no exception | Initial fleet gate: 8 passed. Expanded gate includes known SHA-256 assignments, input order, newcomer-only movement, preserved/disposed scopes, departure, readiness, membership failure/backoff, UUIDv4 stability, fractional TTLs, and real broker tests |
| Tenant cleanup | Parent cancellation surfaced `AggregateException` instead of null; removal timed out before the directory could acknowledge the change | Independent cancellation guards, cancel-all-before-await, task observation, stop callback isolation and disposal pass. Combined cleanup/startup/tenant restart gate: 12 passed |
| Startup validation | Seven projector/direct-host cases expected `ArgumentException`; no exception was thrown | Invalid batch size, blank generation, and nonpositive polling intervals reject before startup or DI mutation; fleet options validate before registration/acquisition |
| Structured concurrency errors | Four cases misclassified errors: code `2001` with unrelated wording stayed a `StreamException`; misleading wording with another/null code became a concurrency exception | Structured-only mapping: 8 persistence failure tests passed, including append/commit failures plus rollback/disposal failures and retained pending audits |

Supplemental regressions were added after those initial red runs: interrupted atomic rebuild
resume across three tenant/pattern identities, blank identity inputs, uncertain rebuild commits
with failed authoritative reload, inventory failure during active work, subsecond TTL rounding,
and broker membership/TTL scenarios. These are additional green coverage; no separate initial
red is claimed for each supplemental case.

## Broker evidence and qualification limit

Compose used the repository-pinned broker image and the default local WebSocket endpoint.
Real membership tests verify that one healthy worker first holds every partition, then releases
exactly the assignments won by a newcomer, retaining unchanged fencing authorities. Graceful
departure and a disposed client connection both return those assignments to the survivor.
A separate test keeps an unmanaged holder's connection alive without renewing its membership
or partition leases: no takeover occurs in the first 300ms, and takeover follows their
2-second TTL expiry. This distinguishes TTL expiry from broker disconnect cleanup; it is not
a process-kill experiment.

The isolated packed consumer reproduced this real stale-append exception:

```text
Cntryl.Fitz.Errors.StreamException
Code=APPEND_FAILED
DomainCode=null
Status=1
```

The pinned client exposes `DomainCode`, but does not populate it on APPEND failures. Strict
`DomainCode == 2001` translation cannot normalize that response. Two existing assertions are
retained unchanged and fail:

- `FitzBrokerIntegrationTests.ShouldThrowConcurrencyExceptionWhenAppendingWithStaleExpectedVersion`
- `AggregatePersistenceTests.CompetingRaisedEventsStillConflictAndKeepPendingChanges(fitz: true)`

Both expect `EventStreamConcurrencyException` and receive the original `StreamException`.
Completing this gate requires an upstream protocol/client version that carries domain code
2001, followed by a coordinated pin/lock update and broker qualification. Falling back to
message matching would contradict the requested contract.

## Final verification

- All 320 baseline tests remain present, with 35 additional cases and no skips.
- Release solution build: zero warnings/errors.
- Solution format verification: passed.
- Updated package locks and solution `dotnet restore --locked-mode`: passed.
- Complete Compose-backed Release suite: **353 passed, 2 failed, 0 skipped (355 total)**.
  The failures are the two upstream-dependent OCC assertions listed above.
- Packed all eight Portia packages at local-only version `0.0.0-architecture-local`.
  An isolated project with its own package cache and no project references restored and
  executed the scoped checkpoint, rebuild, fleet registration/membership, and concrete Fitz
  dependency contracts. Its locked restore also passed. It references `Portia.Fitz`, not
  `Cntryl.Fitz` directly, verifying the concrete dependency flows transitively.

Commands used:

```sh
docker compose up -d --wait
dotnet restore Portia.slnx --locked-mode
dotnet format Portia.slnx --verify-no-changes --no-restore
dotnet build Portia.slnx -c Release --no-restore
dotnet test Portia.slnx -c Release --no-build --no-restore
dotnet pack Portia.slnx -c Release --no-restore \
  -p:Version=0.0.0-architecture-local \
  -p:PackageOutputPath=/tmp/portia-architecture-packages
```

Raw local red/green, restore, build, format, packed-consumer, and complete-suite logs are
retained in ignored `artifacts/architecture-evidence/`. The packed consumer's source and
project are retained there too. These local artifacts are not committed release evidence.

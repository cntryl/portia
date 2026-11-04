# Processor status review: pre-adversarial Portia 0.10.0 baseline

Current release evidence is recorded in the [final 0.10.0 qualification](../release-0.10.0/README.md).

This directory preserves the **pre-adversarial baseline** for stable **0.10.0**, including the
dependency refresh to Fitz .NET 4 / broker 0.4.0 / Wire 3. The later
[adversarial review](../adversarial-0.10.0/README.md) reproduced eleven additional defects; all eleven
fixes are now implemented with regressions in the ordinary test projects. Its
[tdd-results.json](../adversarial-0.10.0/tdd-results.json) records the final post-fix counts and scope.
The source, package hashes, and results here identify the earlier snapshot and do not qualify
the current tree.

Local qualification, hosted CI, package publication, and deployment are separate evidence
boundaries. Package version 0.10.0 has no RC suffix.

## Contracts under review

- One status RPC follows each projector/reactor workload owner. Registration failures do not
  stop processing; revoked owners cannot dispatch status, and failed withdrawals remain retryable.
- Status loads the native checkpoint. Source and failure-journal outages return partial
  observations without replacing that checkpoint. Inspection does not replay the tenant directory.
- Backlog probes are bounded. Expiry, cursor gaps, concurrent appends, and larger histories must
  preserve unknown measurements or lower bounds rather than invent exact counts.
- Operator access requires the configured role on an authenticated identity, honoring its role
  claim type. Existing scoped policies can restrict status; direct operations still require their
  separate explicit permissions and repair approvals.
- Queue publication exposes initial delay and lifetime without changing existing enqueue calls.
  Native NACK releases the same reservation, preserving request identity and causal metadata.
  Renewal stops before settlement. Lost reservations and unknown acknowledgment outcomes cannot
  trigger NACK. Fitz owns attempt limits and dead letters; Portia synthesizes no attempt count.

## Executable regressions

The original status/progress review established failures before the fixes for missing-policy
status access, substituted native checkpoints, tenant-directory reads, gaps and expiry, concurrent
head observations, endpoint withdrawal/reacquisition, registration outages, and unguarded operator
surfaces. Those initial red runs used Fitz .NET 3. Their final behavior is requalified on Fitz 4.

The additional Fitz 4 and operator regressions established these failures against unchanged
runtime/generator implementations:

| Regression group | Red result | What it established |
| --- | --- | --- |
| Queue runner | 9 failed | Configured delay ignored across six incomplete paths; invalid delay accepted; release failure not observed. |
| Native queue consumer | 7 failed | Native NACK absent, explicit retry unavailable, and settlement guards missing. |
| Queue publisher | 6 failed, 8 passed | Requested native delay/TTL not forwarded; existing default/failure controls remained valid. |
| Sender-only generators | 6 failed | Options-only publishers lacked inferred transport registration and required JSON diagnostics. |
| Operator identity and shutdown | 5 failed | Unauthenticated secondary role elevated access; final withdrawal used a fresh timeout per endpoint. |

Positive controls include authenticated secondary identities, custom role claim types,
default-only unsupported-NACK fallback, explicit unsupported-delay rejection, lost reservations,
failed acknowledgments, and terminal-observer failures. The native broker test accepts TTL and
checks initial delay, delayed NACK, preserved identity, redelivery, and final ACK. It does not wait
an hour to establish expiry; native lifetime enforcement remains a Fitz qualification boundary.

Failing test names and counts are retained in [red-regressions.txt](red-regressions.txt).
Local red logs are retained under `/tmp/portia-queue-*-red.log` and
`/tmp/portia-operator-guards-red.log`. Raw status-review logs use `/tmp/portia-status-*.log`,
`/tmp/portia-operator-guard-red.log`, and `/tmp/portia-admin-surface-red.log`.

## Advisory Jev review

Three bounded requests used the pinned `jev-1.14.0` model. The request files retain the supplied
source snapshots and typed questions; responses retain probabilities and usage. No credentials
are included.

- [Initial request](jev-before-request.json) and [response](jev-before-response.json).
- [Status follow-up request](jev-after-request.json) and [response](jev-after-response.json).
- [Queue/operator request](jev-queue-and-operator-request.json) and
  [response](jev-queue-and-operator-response.json), including source hashes.

Jev helped prioritize inspection. It did not detect every proven defect: the initial review gave
low probability to native-checkpoint substitution and concurrent-count errors. Follow-up answers
also retained uncertainty about discovery guards, unsupported publication controls, and shared
shutdown budgets. Source inspection and red-to-green regressions decide acceptance; these
probabilities are not test results or a release-readiness score.

## Historical release-preparation evidence

The pre-adversarial candidate passed the following local checks on macOS Arm64, SDK 10.0.403 and
runtime 10.0.14. All framework tests used Fitz .NET 4; native broker checks used the pinned 0.4.0
image. These counts and artifacts are historical, not the post-fix release candidate's results.

| Check | Result |
| --- | --- |
| Solution formatting | 0 files changed; no workspace warnings in the diagnostic verification. |
| Release build | 0 warnings, 0 errors. |
| Main tests, including broker, Cassie, and S3 integration | 1,500 passed. |
| Compiler and consumer tests, isolated from other qualification processes | 822 passed. |
| MCP tests | 93 passed. |
| Reflection-disabled tests | 3 passed. |
| Isolated persistent-broker crash/restart recovery | 1 passed against final binaries. |
| Framework total across those separate runs | **2,419 passed, 0 failed, 0 skipped.** |
| Stable external dependency check | Passed. |
| NuGet vulnerability audit, including transitives | No known vulnerabilities reported. |
| Local package family | 16 packages at 0.10.0; internal dependency versions aligned. |
| Fresh packed compiler consumers | Required diagnostics and HTTP fallback passed, with and without the independent analyzer package. |
| Fresh managed consumers | Projection store, Cassie, MCP testing, and object storage passed. |
| Actual NativeAOT publish and execution | HTTP, MCP/admin, and Fitz/LexKey/status passed for `osx-arm64`. |
| Reflection-disabled runtime matrices | Request, aggregate, envelope, generated HTTP, and mixed processors passed. |
| Packaged Accounts example | Tests, projected balance, and actual OTLP export passed. |

The status package smoke resolves `IProcessorStatusClient` and round-trips its request/response,
failure, and opaque cursors with JSON reflection disabled and no application-owned status JSON
registration. NativeAOT results are local macOS results; Linux remains a hosted CI gate.

An earlier combined solution run aborted its consumer test host. The cause was not established.
Subsequent isolated runs passed 808 tests before the new queue cases and 822 afterward. Do not
report the aborted run as green. The Accounts run initially encountered the integration broker's
occupied port; after stopping completed integration services, its isolated stack passed and cleaned
up.

All declared external package IDs were checked against their registries. Microsoft.OpenApi and
YamlReader remain on 2.13.0 because ASP.NET Core 10 requires the 2.x line. Upstream-owned transitive
dependency versions were not forcibly overridden. `dotnet list ... --outdated --include-transitive`
failed with `Sequence contains no matching element` for two Roslyn-heavy projects; per-project
direct checks and the registry inventory covered declared packages, and the independent solution
vulnerability audit completed successfully. This is not a claim that every transitive package is
the latest available major version.

[results.json](results.json) records the baseline result summary; [source-sha256.json](source-sha256.json)
identifies the earlier uncommitted source/API snapshot and base commit;
[artifact-sha256.txt](artifact-sha256.txt) identifies its 16 local packages. Commands and full local
logs use `/tmp/portia-010-*`. Those package checks used `PackageVersion=0.10.0` and
`PortiaSmokePackageVersion=0.10.0`, with standalone consumers restoring against `artifacts/packages`
in a fresh `NUGET_PACKAGES` cache. Post-fix evidence belongs to
[tdd-results.json](../adversarial-0.10.0/tdd-results.json), rather than these baseline hashes.
The original package bytes for these historical hashes are preserved locally under
`artifacts/packages-before-tdd/`; the later fixes rebuilt `artifacts/packages` at the same unpublished
0.10.0 version.

Exact-commit hosted CI, including Linux NativeAOT and remaining CI campaigns, is still required.
No commit, release tag, registry publication, or production deployment was performed here. The
migration contract is in the [0.10.0 guide](../../migration-0.10.0.md) and the
[Fitz 4 guide](../../fitz-4-upgrade.md).

Known limits remain explicit: RPC availability does not prove a dead worker is alive; a sampled
checkpoint is not an immutable cross-system snapshot; queue acceptance is best effort; and a
processed message does not independently establish business completion. Expected business
rejections require their recorded outcome and explicit resolution under the
[validation authority](../../validation-authority.md) model.

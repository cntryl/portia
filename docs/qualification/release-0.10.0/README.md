# Portia 0.10.0 release qualification

All confirmed defects from both adversarial reviews are resolved with ordinary regression tests.
The stable package version is **0.10.0**. This record describes final local qualification;
exact-commit hosted CI and publication are separate gates. Release and registry readback details
are recorded with the [0.10.0 release](https://github.com/cntryl/portia/releases/tag/v0.10.0).

| Check | Local result |
| --- | --- |
| Formatting | 0 of 1,068 files changed; no workspace warnings. |
| Release build | 0 warnings, 0 errors. |
| Main suite, including Fitz, Cassie, and S3 integration | 1,584 passed. |
| Isolated compiler/consumer suite | 855 passed. |
| MCP / reflection-disabled / isolated crash recovery | 93 / 5 / 1 passed. |
| Framework total | **2,538 passed, 0 failed, 0 skipped.** |
| Packaged Accounts example | 10 tests, projected balance, and actual OTLP export passed. |
| Namespace Fix All | 64 files, generated constructor references, build, and idempotence passed. |
| Package family | 16 stable packages; 20 internal dependency declarations aligned to 0.10.0. |
| Packed compiler controls | Diagnostics and HTTP fallback passed with and without independent analyzers. |
| Fresh managed consumers | Projection store, Cassie, MCP testing, and object storage passed. |
| Actual NativeAOT consumers | HTTP, MCP/admin, and Fitz/LexKey/status published and executed for macOS Arm64. |
| Reflection-disabled runtime controls | Request, aggregate, envelope, generated HTTP, and mixed processors passed. |
| Dependencies | Stable dependency verification passed; the transitive vulnerability audit reported no known vulnerabilities. |

The HTTP NativeAOT consumer exercises the generated event-catalog entry point, source-generated
resolver-renamed and reordered HTTP binding, and the actual OpenAPI document. Fitz status metadata
composes without application-owned status JSON declarations. New effect-ID validation preserves
the exact legacy bytes for accepted identities; ambiguous configurations fail before deriving an
aliased key. The [migration guide](../../migration-0.10.0.md) explains the affected rare identities.

[results.json](results.json) records the counts and evidence boundaries;
[green-results.txt](green-results.txt) retains concise outputs;
[source-sha256.json](source-sha256.json) identifies the 1,136 frozen inputs;
[artifact-sha256.txt](artifact-sha256.txt) identifies local package bytes. Local packages were built
before the release commit; their hashes are not asserted to equal the later registry artifacts.
Every packed consumer restored with a fresh NuGet cache.

The initial hosted run exposed a race in a new cleanup-fault fixture: the listener could unregister
its cancellation callback before it ran. The outer test now retains that registration through
supervision, preserving the exact three-error assertion. Runtime code is unchanged. The seven
focused cleanup cases and 1,541 broker-free main tests passed afterward; the original
[hosted failure](initial-ci-fixture-failure.txt) is retained and fresh exact-commit CI is required.
The next hosted run exposed the same lifetime issue in the coordinator fixture. Both coordinator
cases now retain their registrations through coordination, and the revocation test confirms callback
execution explicitly. An audit of all eleven registration sites found no other matching asserted
callback-fault race. The [second failure](second-ci-fixture-failure.txt) is retained; publication
continues to require successful checks on the updated commit.

The [first review](../adversarial-0.10.0/README.md) retains eleven findings and its RED evidence.
The [second review](../adversarial-0.10.0-pass2/README.md) retains nine more findings, eleven original
failing cases, and three additional effect-identity failures. Both retain their original source and
proof hashes. Current tests include positive controls and expanded lifecycle, compatibility, and
compiler cases. Jev provided advisory triage; source and executable regressions decide acceptance.

Publication is permitted only after the latest required CI and CodeQL results pass for the exact
release commit. Hosted CI qualifies Linux NativeAOT, compiler benchmark controls, Prometheus,
namespace fixes, packed consumers, Accounts/OTLP, and broker/storage/Cassie/crash integration.
`publish.yml` stays simple: the release operator verifies those gates before dispatching it, then
reads back all sixteen registry versions, the tag, and a fresh registry-only consumer.

No production deployment or application data migration is part of package publication. Existing
read-model versioning remains an application concern. Processor status is an operator observation,
not a cross-system business consistency guarantee; preserve the
[validation authority](../../validation-authority.md) contract when handling outcomes.

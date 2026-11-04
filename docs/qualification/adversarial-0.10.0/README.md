# Adversarial review of Portia 0.10.0

This is the historical first-review qualification. See the [final release qualification](../release-0.10.0/README.md)
for the later fixes and current release evidence. Its package bytes are preserved locally under
`artifacts/packages-before-release/`; the original pre-TDD bytes remain in `artifacts/packages-before-tdd/`.

Reviewed on 2026-11-03. This directory preserves the **historical RED baseline: eleven P2 defects
reproduced before their fixes**. All eleven fixes are now implemented, and their regressions live in
the ordinary test projects. The original proofs, logs, results, and hashes remain unchanged.
[tdd-results.json](tdd-results.json) records the final post-fix counts and validation
scope; the RED counts below describe the reviewed snapshot, not the current working tree.

The trigger and scope of each finding matter; none was classified as an unconditional failure of
every application.

The review held all 547 product source/project/API files unchanged. Independent passes covered
aggregate and request execution, projector/reactor outcomes and checkpoints, recovery, Fitz and
Cassie persistence, transport and workload lifetimes, generators/analyzers/code fixes, HTTP,
JSON, MCP/admin, package/public API boundaries, JWT, storage, testing, and telemetry. Findings
were checked against [validation authority](../../validation-authority.md).

Five bounded `jev-1.14.0` API calls asked 23 typed questions against focused source excerpts.
Source inspection and executable proofs established the findings. The final isolated runs had
**22 cases: 17 reproduced failures and five passing controls**, with no outer compilation errors.
Generated compiler errors inside the escaped-identifier tests are the defect under test.

## Post-fix local qualification

All eleven findings are resolved with ordinary regression fixtures and additional positive controls.
The two follow-ups below also failed before their fixes and pass in the final suites.

| Check | Final result |
| --- | --- |
| Formatting and Release build | 0 files changed; 0 warnings and 0 errors. |
| Main tests, including Fitz, Cassie, and S3 integration | 1,550 passed. |
| Isolated compiler/consumer suite | 836 passed. |
| MCP / reflection-disabled / isolated crash recovery | 93 / 5 / 1 passed. |
| Framework total | **2,485 passed, 0 failed, 0 skipped.** |
| Packaged Accounts example | 10 tests, projected balance, and actual OTLP export passed. |
| Stable package family | 16 packages; 20 internal dependency declarations aligned to 0.10.0. |
| Fresh packed consumers | Compiler fixtures, four managed consumers, and three actual macOS Arm64 NativeAOT consumers passed. |
| Runtime controls | Request, aggregate, envelope, generated HTTP, and mixed processors passed with reflection disabled. |
| Dependencies | Stable dependency verification passed; the solution audit including transitives reported no known vulnerabilities. |

The NativeAOT HTTP consumer exercises source-generated, resolver-renamed and reordered member
metadata through both the real endpoint and OpenAPI. All consumer restores used fresh NuGet caches.
[tdd-green.txt](tdd-green.txt) retains the concise run results;
[tdd-source-sha256.json](tdd-source-sha256.json) identifies 1,113 unchanged source, API, test, smoke,
and qualification inputs; [tdd-artifact-sha256.txt](tdd-artifact-sha256.txt) identifies the new packages.
The original package bytes remain locally in `artifacts/packages-before-tdd/`.

This is local qualification of an uncommitted working tree. Exact-commit hosted CI, including Linux
NativeAOT, remains a release gate. No commit, tag, publication, production migration, or deployment
was performed. Test integration services were stopped and the Accounts qualification stack removed.

## Current regression fixtures

The promoted fixtures run through ordinary project test commands, without injecting
`AdversarialProofs.targets`:

| Finding | Current fixture |
| --- | --- |
| F01 | [HTTP member binding](../../../test/Portia.Tests/Http/HttpBindingMetadataIdentityTests.cs), [generated endpoint](../../../test/Portia.ConsumerTests/Http/HttpBindingMetadataIdentityConsumerTests.cs) |
| F02 | [Identity validation and key compatibility](../../../test/Portia.Tests/Fitz/FitzKvCheckpointIdentityTests.cs), [native checkpoint isolation](../../../test/Portia.Tests/Fitz/FitzCheckpointIdentityCollisionTests.cs) |
| F03 | [Projection batch ownership](../../../test/Portia.Tests/Fitz/FitzKvProjectionOverlapTests.cs) |
| F04 | [Origin scheme](../../../test/Portia.Tests/Http/HttpOriginSchemeTests.cs) |
| F05 | [Manual reactor rebuild rejection](../../../test/Portia.Tests/Reactions/ManualReactorRebuildTests.cs) |
| F06 | [Repair queue reconciliation](../../../test/Portia.Tests/Operations/OperationalRepairQueueProof.cs) |
| F07 | [Event catalog atomic registration](../../../test/Portia.ConsumerTests/Serialization/DomainEventTypeCatalogAtomicityTests.cs) |
| F08 | [Conditional sender discovery](../../../test/Portia.ConsumerTests/Components/ConditionalSenderDiscoveryTests.cs) |
| F09 | [OpenAPI member metadata](../../../test/Portia.ConsumerTests/Http/OpenApiJsonMemberMetadataTests.cs) |
| F10 | [Escaped permission identifiers](../../../test/Portia.ConsumerTests/Components/EscapedPermissionIdentifierTests.cs) |
| F11 | [Generation-aware KV example](../../../test/Portia.Tests/Fitz/FitzKvDocumentedRebuildSafetyTests.cs) |

### Post-fix follow-up coverage

These follow-ups expand F06 and F01 coverage; they do not change the original eleven findings or
the historical RED counts below. Final post-fix counts are recorded in
[tdd-results.json](tdd-results.json).

- **F06, claimed projection repair:** a claimed retry's projection transaction commits, but its
  acknowledgment is lost while retries remain. The
  [regression](../../../test/Portia.Tests/Operations/OperationalProjectionRepairProgressTests.cs)
  requires authoritative checkpoint advancement to mark that repair `indeterminate`, disengage
  its stale retry source set, invalidate obsolete queued siblings, and preserve the block until a
  newly approved `resume` plan runs. The original repair remains `indeterminate` after resumption.
  [Follow-up RED evidence](tdd-repair-progress-red.txt) records the failing case before this guard.
- **F01, legacy unnamed HTTP helpers:** previously generated helper calls must still identify the
  HTTP constructor's member when the JSON constructor uses a different parameter order. The
  [expanded binding fixture](../../../test/Portia.Tests/Http/HttpBindingMetadataIdentityTests.cs)
  checks both body-member discovery and rejection of conflicting routed body values.
  [Follow-up RED evidence](tdd-legacy-binding-red.txt) records two failures. The final binding fixture
  passes, including an explicit alias and member-converter control.

## Validated findings: original pre-fix behavior

The descriptions and remediation instructions below record the original findings. Source line
references refer to the reviewed snapshot; linked product files now include the fixes.

### F01 — Renamed HTTP metadata can swap constructor arguments

**P2.** [PortiaHttpBinding.cs:250](../../../src/Portia.AspNetCore/Binding/PortiaHttpBinding.cs#L250)
falls back to `JsonTypeInfo.Properties[constructorMemberIndex]` when a resolver changes a property
name. JSON property order need not equal constructor order. A supported class with constructor
`(a, b)`, properties declared `B, A`, and a resolver renaming the fields to `wire_a, wire_b` accepts
`11,22` and silently delivers **`22,11`** to the handler with HTTP 200.

Match resolved metadata to its constructor/member identity, such as `AssociatedParameter`, rather
than assuming ordinal equality. Preserve the identity in the binding cache. Proofs:
[helper](proofs/main/HttpBindingMetadataIdentityTests.cs) and
[actual generated HTTP endpoint](proofs/consumer/HttpBindingMetadataIdentityConsumerTests.cs).
The ordinary naming path is unaffected; explicit metadata renaming and different orders trigger it.

### F02 — Distinct admitted checkpoint identities alias one durable key

**P2.** [FitzKvCheckpoints.cs:35](../../../src/Portia.Fitz/Projections/FitzKvCheckpoints.cs#L35)
joins identity fields with NUL, while stream resource and rebuild identifiers admit that character.
For the same component/realm/area, `(resource="order\0part", rebuild="v2")` and
`(resource="order", rebuild="part\0v2")` have the same workload route and checkpoint key.
Saving cursor `8` for the first makes the second load **`8` instead of Start**. A consumer can
therefore resume from another identity's progress.

Use unambiguous field encoding, or reject the separator before persistence with an explicit
compatibility decision. Proofs cover [key/store behavior](proofs/main/FitzKvCheckpointIdentityTests.cs)
and the [actual Fitz broker](proofs/main/portia-checkpoint-identity-collision-proof.cs).
This requires unusual but currently accepted identifiers; ordinary UUID resource names do not collide.

### F03 — Overlapping projection begins can split data from its checkpoint

**P2.** [FitzKvProjectionStore.cs:127](../../../src/Portia.Fitz/Projections/FitzKvProjectionStore.cs#L127)
checks `_open` before awaiting native transaction acquisition and fills it afterward. Two calls on
one repository instance, for different workload routes, can both pass the guard. The second
assignment retargets the repository's `Transaction`: a write intended for batch A enters B, while
A successfully commits its checkpoint without that write.

Reserve the single-batch slot before awaiting and release it on acquisition failure. The
[deterministic barrier proof](proofs/main/FitzKvProjectionOverlapTests.cs) verifies the missing
data with the committed checkpoint; it also accepts the correct behavior of rejecting the second
begin. This is a shared/manual-instance concurrency case, not the normal fresh-scope hosted path.

### F04 — Same-origin fallback ignores the request scheme

**P2.** [PortiaCrossOrigin.cs:72](../../../src/Portia.AspNetCore/Configuration/PortiaCrossOrigin.cs#L72)
compares host/default port without `request.Scheme`. Without `Sec-Fetch-Site`, an HTTPS target
accepts `Origin: http://example.test` when its host is `example.test`, despite no allowing CORS
decision. The reverse scheme mismatch also passes.

Compare scheme, normalized host, and effective port. The [proof](proofs/main/HttpOriginSchemeTests.cs)
has two failing cross-scheme cases and two passing same-scheme controls. Exploitation needs the
missing fetch metadata, a hostile same-host alternate-scheme origin, and ambient credentials the
browser sends. Modern fetch metadata and other application protections can prevent that path;
this is a defect in the fallback origin contract, not a general authentication bypass.

### F05 — Manual reactors silently accept projector rebuild options

**P2.** [ReactorRunner.cs:57](../../../src/Portia.Core/Reactions/ReactorRunner.cs#L57) validates a
nonblank `ProjectionRunOptions.RebuildId` but constructs the live checkpoint identity without it.
Both single and batch manual runs read events, execute effects, and update live progress instead
of rejecting the option. Hosted [WorkloadRegistration](../../../src/Portia.DependencyInjection/Workloads/WorkloadRegistration.cs#L24)
correctly refuses the same configuration.

Reject nonnull rebuild IDs at the public/manual runner boundary before reading or applying effects.
The [two-shape proof](proofs/main/ManualReactorRebuildTests.cs) exercises the shared manual path.

### F06 — Resolving one repair leaves sibling plans queued

**P2.** [OperationalSession.cs:107](../../../src/Portia.Core/Operations/OperationalSession.cs#L107)
returns early once the block is clear. Two previews of the same failure can both be approved and
queued. Successful execution marks only the selected plan committed; the sibling remains
**queued after the failure is resolved**. Its status pins its plan in retention and misreports
pending operator work while that owner continues running. Reacquisition can invalidate it, so
this is not a claim that restart can never clear it.

Invalidate sibling plans whose failure/progress is no longer current when the block resolves,
including the success and authoritative-checkpoint reconciliation paths. The
[proof](proofs/main/OperationalRepairQueueProof.cs) uses the real reactor runner and operational API.

### F07 — Rejected event registration leaves an undeclared alias

**P2.** [DomainEventTypeCatalog.cs:35](../../../src/Portia.Abstractions/Events/DomainEventTypeCatalog.cs#L35)
adds the name/version lookup before checking whether the CLR type is already registered.
Registering `E` under `(a,1)`, then catching the failed registration of `E` under `(b,2)`, leaves
`TryResolve(b,2)` succeeding while `Describe(E)` still reports `(a,1)`. It also prevents another
event type from legitimately claiming `(b,2)`.

Validate both constraints before mutating either lookup, or roll back the first mutation.
[Proof](proofs/consumer/DomainEventTypeCatalogAtomicityTests.cs). This matters when composition
recovers from registration errors; a host that aborts immediately does not continue using the catalog.

### F08 — Conditional publication omits inferred transport registration

**P2.** [RegistrationCallInterceptorGenerator.cs:58](../../../src/Portia.Generators/Components/RegistrationCallInterceptorGenerator.cs#L58)
filters outbound calls to `MemberAccessExpressionSyntax`. Valid `publisher?.EnqueueWithOptionsAsync(...)`
uses `MemberBindingExpressionSyntax`. In a publisher-only application relying on dispatch inference,
the code compiles but the transport catalog lacks the request, causing runtime publication failure.
JSON diagnostics already recognize the conditional form.

Handle conditional invocation syntax in descriptor discovery, or diagnose it explicitly. The
[proof](proofs/consumer/ConditionalSenderDiscoveryTests.cs) includes a passing ordinary publication
control and a passing JSON diagnostic control. Explicit handler/request registration can mask it.

### F09 — OpenAPI advertises a different JSON member name from runtime binding

**P2.** [PortiaOpenApiOperationTransformer.cs:170](../../../src/Portia.AspNetCore/OpenApi/PortiaOpenApiOperationTransformer.cs#L170)
builds body names from the declared wire name/naming policy without consulting the resolved
`JsonPropertyInfo`. A supported resolver rename to `custom` is accepted by the actual endpoint,
but OpenAPI still describes the original `value` member. Generated clients and interactive callers
therefore send the wrong contract.

Derive the schema from the same resolved per-member JSON metadata used for binding. The
[proof](proofs/consumer/OpenApiJsonMemberMetadataTests.cs) first verifies HTTP 200 for the renamed
runtime field, then reads the real OpenAPI document and shows that `custom` is absent.

### F10 — Escaped permission properties generate invalid C#

**P2.** [RegistrationCallInterceptorGenerator.cs:523](../../../src/Portia.Generators/Components/RegistrationCallInterceptorGenerator.cs#L523)
emits raw `property.Name` in permission token access. Valid request properties `@event` and
`@class`, referenced by `{event}` or `{class}`, produce `typed.event`/`typed.class` and compiler
errors CS1073/CS1001.

Escape emitted C# identifiers consistently. The [proof](proofs/consumer/EscapedPermissionIdentifierTests.cs)
includes both failing keywords and a passing ordinary `Id` control.

### F11 — The documented Fitz KV delta example is unsafe for opt-in rebuilds

**P2, documentation/example.** The [balance repository example](../../projectors-and-reactors.md#fitz-kv-persistence)
uses unversioned `balance\0{id}` keys. Selecting a distinct `RebuildId` gives a separate checkpoint
but uses the same model rows. Replaying a `+10` event changes the currently queried live balance
from **10 to 20** while the live checkpoint remains unchanged.

The abstract base deliberately leaves model keys to the application; this is not a claim that
Fitz KV promises automatic model isolation. Make the example generation-aware, or explicitly
state that it cannot safely opt into rebuilds until its data namespace is isolated. The
[native-broker proof](proofs/main/portia-kv-rebuild-example-proof.cs) copies the documented key/delta
logic. It does not require introducing a read-model version feature.

## Historical proofs and reproduction

The original reproducers remain isolated under `proofs/`. At the RED baseline, the ordinary projects
and test gates were restored to their pre-review source, and product fixes had not been applied.
`AdversarialProofs.targets` injected those archived fixtures only when explicitly selected. The
operational repair fixture was a partial class using the original test's helpers.

**Do not inject the archived targets into the current tree.** Their fixtures have been promoted to
ordinary tests, so injection would introduce duplicate definitions. Run the ordinary projects for
current validation and consult [tdd-results.json](tdd-results.json) for the post-fix evidence.

<details>
<summary>Original reproduction commands: recorded pre-fix snapshot only</summary>

These commands require the reviewed source snapshot with the promoted fixture files absent. After
the usual repository restore, the pinned broker supplied the two native proofs:

```sh
docker compose up -d --wait fitz
dotnet test test/Portia.Tests/Portia.Tests.csproj -c Release --no-restore -m:1 \
  -p:CustomAfterMicrosoftCommonTargets="$PWD/docs/qualification/adversarial-0.10.0/AdversarialProofs.targets" \
  --filter 'FullyQualifiedName~ManualReactorRebuildTests|FullyQualifiedName~ShouldInvalidateOtherQueuedRepairsWhenFailureIsResolved|FullyQualifiedName~HttpOriginSchemeTests|FullyQualifiedName~HttpBindingMetadataIdentityTests|FullyQualifiedName~FitzKvCheckpointIdentityTests|FullyQualifiedName~FitzKvDocumentedRebuildSafetyProof|FullyQualifiedName~FitzKvProjectionOverlapTests|FullyQualifiedName~FitzCheckpointIdentityCollisionProof'
dotnet test test/Portia.ConsumerTests/Portia.ConsumerTests.csproj -c Release --no-restore -m:1 \
  -p:CustomAfterMicrosoftCommonTargets="$PWD/docs/qualification/adversarial-0.10.0/AdversarialProofs.targets" \
  --filter 'FullyQualifiedName~ConditionalSenderDiscoveryTests|FullyQualifiedName~DomainEventTypeCatalogAtomicityTests|FullyQualifiedName~EscapedPermissionIdentifierTests|FullyQualifiedName~HttpBindingMetadataIdentityConsumerTests|FullyQualifiedName~OpenApiJsonMemberMetadataTests'
```

Archived results: main **11 failed / two passed**, consumer **six failed / three passed**.
Full output is retained in [main-proof.txt](main-proof.txt) and [consumer-proof.txt](consumer-proof.txt).
After an injected run, use an ordinary solution build before a normal `--no-build` test run so the
test assemblies no longer contain the injected fixtures. Stop the test broker when finished.

</details>

[results.json](results.json) records the original RED counts and Jev usage;
[review-source-sha256.json](review-source-sha256.json) freezes the reviewed product source and base
commit; [proof-sha256.json](proof-sha256.json) identifies the fixtures. The earlier
[status qualification](../processor-status-review/README.md) remains historical evidence of its
2,419 tests but predates these adversarial cases. Neither historical result substitutes for the
post-fix validation in [tdd-results.json](tdd-results.json).

## Jev evidence and rejected candidates

Requests and responses are retained for [tooling](jev-tooling-request.json),
[recovery/rebuild](jev-recovery-rebuild-request.json), [HTTP/identity](jev-http-identity-request.json),
[other boundaries](jev-boundaries-request.json), and [schema/permissions](jev-schema-permissions-request.json).
Each request's matching `*-response.json` file contains the model answers and usage; no credentials
are included.

The model missed the catalog mutation (0.12 probability despite the failing proof), and favored
the static-import hypothesis (0.84) that the compiler rejected as invalid C#. That candidate was
withdrawn. HTTP origin omission, manual rebuild options, and the unsafe rebuild example received
stronger signals, but those signals were still validated independently. Probabilities did not
become findings automatically, and questions were not retuned to obtain higher scores.

Other reviewed cases were not elevated: open generic requests are documented as unsupported;
queue/notice fixed-route enforcement lacks a clear receiving guarantee and normal request
authorization still applies; shared tenant buckets violate the documented resolver contract.
Cooperative JWT cancellation, principal claim provenance, and fake cleanup after invalid receipts
did not establish an additional violation of the promised execution contract. These decisions do
not certify arbitrary application policies or provider implementations.

This is a local adversarial source review with focused reproductions, not hosted CI, a formal
proof of correctness, publication, or a production migration qualification.

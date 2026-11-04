# Second adversarial review of Portia 0.10.0

This directory preserves the second review's **historical RED baseline**. Nine additional P2
defects were reproduced against the previously qualified 0.10.0 candidate. Independent passes
covered domain execution and persistence, workers and transports, compiler tooling and HTTP/MCP,
and storage, authentication, testing, telemetry, and packaging. The reviewed 1,113 source, API,
test, smoke, and qualification inputs remained unchanged throughout these reproductions.

The subsequent instruction to resolve every defect and publish 0.10.0 authorizes implementation.
Original proofs and RED logs remain immutable here; current regressions belong in the ordinary
test projects. All nine mechanisms are now implemented with passing focused regressions. The
[final release qualification](../release-0.10.0/README.md) records complete validation. Publication
requires all confirmed defects to be resolved and qualification to pass.

## Confirmed findings

All findings are P2 and have specific triggers. They are not unconditional failures of every host.

| Finding | Trigger and observed failure | RED proof |
| --- | --- | --- |
| R2-01: durable attempt start is not idempotent | A journal write persists and loses its acknowledgement. Retrying appends the same attempt ID twice; reactor completion and failure recording then throw instead of committing progress. | [Attempt start](proofs/main/Pass2OperationalStartAcknowledgementTests.cs): two failures, one control. |
| R2-02: fatal listener failure is hidden | A queue terminal observer throws while a notice listener remains healthy. The combined background service stays pending, hiding the dead queue listener from host failure handling. | [Actual Fitz host](proofs/main/Pass2FitzCombinedWorkerFailureTests.cs): one failure, one control. |
| R2-03: failed tenant extraction skips a record | The application tenant extractor throws. The resumable cursor has already advanced and omits that start or stop record when enumerated again. | [Tenant cursor](proofs/main/Pass2TenantCursorFailureTests.cs): two failures. |
| R2-04: subscription cleanup poisons a cursor | Subscription disposal throws before `_reading` is reset. Every later enumeration is rejected as concurrent. | [Tenant cursor](proofs/main/Pass2TenantCursorFailureTests.cs): one failure; ordinary cleanup control passes. |
| R2-05: revocation does not always await work | A workload cancellation callback throws. The single-process coordinator has removed the entry and exits before awaiting its asynchronous cleanup. | [Coordinator](proofs/main/Pass2CoordinatorCancellationTests.cs): one failure, one control. |
| R2-06: manual effect identities can alias | Admitted manual reactor/effect names containing source UUID separator text serialize distinct tuples identically and produce the same idempotency key. Ordinary names are unaffected. | [Effect identities](proofs/main/Pass2ManualEffectIdentityTests.cs): one failure, one control. |
| R2-07: conditional registration is not generated | A concrete nonnull `app?.AddRequestHandler<Handler>()` compiles but reaches the runtime missing-interceptor exception. | [Registration](proofs/consumer/Pass2ConditionalRegistrationTests.cs): one failure, two controls. |
| R2-08: namespace repair drops a containing type | Moving `Old.Price` rewrites `new Old.Price.Key()` to `new Target.Key()`, producing CS0234. | [Namespace action](proofs/consumer/Pass2NestedNamespaceConstructorTests.cs): one failure, one control. |
| R2-09: friend catalogs are ambiguous | A friend consumer declares events and explicitly calls its generated catalog extension. Both assembly extensions are visible and the call fails with CS0121. | [Friend catalog](proofs/consumer/Pass2FriendCatalogTests.cs): one failure, one control. |

Relevant source locations in the reviewed snapshot:

- [Attempt append](../../../src/Portia.Core/Operations/OperationalSession.cs#L194) and its retry loop.
- [Combined listener lifetime](../../../src/Portia.Fitz/Hosting/FitzApplicationWorkers.cs#L155).
- [Cursor advancement and cleanup](../../../src/Portia.Core/Tenancy/EventSourcedTenantDirectory.cs#L328).
- [Revocation cancellation](../../../src/Portia.Core/Workloads/SingleProcessWorkloadCoordinator.cs#L108).
- [Persisted effect identity](../../../src/Portia.Abstractions/Reactions/Reactor.cs#L98).
- [Registration syntax filter](../../../src/Portia.Generators/Components/RegistrationCallInterceptorGenerator.cs#L112).
- [Constructor namespace rewrite](../../../src/Portia.CodeFixes/Applications/ApplicationStructureCodeFixProvider.cs#L348).
- [Generated catalog entry point](../../../src/Portia.Generators/DomainEvents/DomainEventCatalogGenerator.cs#L181).

The journal proof uses a deterministic store wrapper that persists a write before throwing its
acknowledgement error. It exercises the same unknown-commit boundary the native store can expose;
it does not claim to interrupt a real broker COMMIT response. The combined listener proof uses the
pinned Fitz broker and the real background service, queue consumer, and notice listener.

## Evidence and reproduction

The isolated original RED runs contain **20 cases: 11 reproduced failures and nine passing controls**.
[main-proof.txt](main-proof.txt) records eight failures and five controls;
[consumer-proof.txt](consumer-proof.txt) records three failures and four controls. There were no
outer compilation errors. Compiler errors inside consumer compilations are the asserted defects.

[review-source-sha256.json](review-source-sha256.json) identifies the reviewed snapshot and base
commit. [proof-sha256.json](proof-sha256.json) identifies the eight archived proof sources;
[results.json](results.json) records counts, scope, exclusions, and Jev usage.

`AdversarialProofs.targets` injected the archived fixtures into the pre-fix snapshot through
`CustomAfterMicrosoftCommonTargets`. They were selected with `FullyQualifiedName~Pass2` and run
serially with `-m:1`. Main proofs require the pinned Compose Fitz broker. These are historical
reproducers: after promotion, use ordinary tests instead of injecting archived fixtures into the
current tree. A normal solution build restores ordinary test binaries after an injected run.

Additional R2-06 probes reproduced three more failures in the unchanged effect-ID implementation:
a bound tenant delimiter alias and two malformed UTF-16 aliases. Their [RED log](effect-boundaries-red.txt)
and [source/proof record](effect-boundaries-sha256.json) are separate because independent fixes were
already in progress elsewhere. Accepted legacy IDs retain exact bytes; ambiguous identities now fail
fast with explicit migration guidance.

## Advisory review and exclusions

Three bounded `jev-1.14.0` API requests asked ten typed questions. Exact requests and responses are
retained in the `jev-workers-*`, `jev-compiler-*`, and `jev-journal-*` files, without credentials.
Usage was 14,034 input tokens and 238 output tokens. Several proven defects received low or
uncertain probabilities. Source inspection and failing tests decide acceptance.

The restart-policy asymmetry was not elevated: the existing contract explicitly permits automatic
crash replay. Jev's strong answer about clearing a running repair's block establishes a source
observation, not an additional policy violation. Other exclusions include undocumented `JsonIgnore`
omission expectations for HTTP constructor parameters, intentional exact-name-first JSON matching,
nonconforming providers, mutable aggregate concurrent reuse, and arbitrary payload mutation.
ASP.NET Core enforces the MCP request-size metadata; that candidate was rejected against its
[official routing implementation](https://raw.githubusercontent.com/dotnet/aspnetcore/v10.0.14/src/Http/Routing/src/EndpointRoutingMiddleware.cs).

Additional domain and persistence coverage is recorded in
[the review notes](proofs/main/domain-persistence-review.md). This local review is separate from
subsequent TDD qualification, exact-commit hosted CI, publication, and deployment.

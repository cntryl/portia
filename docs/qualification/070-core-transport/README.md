# Core transport qualification (#114–#117)

Recovered independently from draft #147 onto main `14531deb62555b457965354d8d624c450a13f5fb`.
Deferred MCP runtime changes and failing SDK regressions remain in #147 and its preserved worktree.

- #114: active terminal unexpected-read regression checks one fault, warning and disposition;
  classified, permanent, threshold, ownership and continuation controls remain in QueueRunnerTests.
- #115: LinkedDeliveryRootTests proves independent queue/notice/schedule roots under ambient activity,
  context restoration, missing/invalid contexts, remote sampling and RPC parentage.
- #116: TelemetryCompletionTests covers actual exception classification after caller cancellation,
  stream preflight faults, sampled/propagation-only data and balanced request measurements.
- #117: README commands match CI's two integration categories; scope/design documentation describes
  the already implemented explicit resources/prompts; serializer XML matches generated metadata.

Adversarial source review: terminal fault reporting is confined to unclassified read failures at the
retry threshold; no duplicate reporting for classified failures. Wire remote marking is internal to
received ingress, preserving public parsing. Linked roots explicitly suppress ambient fallback without
mutating Activity.Current manually. Stream disposal faults take precedence over early consumer stop.
Removed documentation claims for deferred schema/HTTP-scope work before delivery.

Local gates: locked full restore, Release build (zero warnings/errors), solution format verification,
1,710 broker/storage-free tests (1,134 core + 502 compiler/consumer + 71 existing MCP + 3
reflection-disabled), zero failures/skips. Real isolated pinned Fitz/Sqrzl tests: 24 passed, zero skips.
Exact commands and raw output accompany this report. Hosted CI supplies fresh packed consumers and
Linux NativeAOT; final-head CI/CodeQL and merged-commit checks remain separate delivery gates.

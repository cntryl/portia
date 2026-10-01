# Runtime and testing qualification (#105–#109)

Baseline: `9145b9df963f47068fbef4a05db9cc5be1b5a0be`. These five related fixes share aggregate histories, bounded processing, and the testing contracts that qualify them. No dependencies or public signatures change.

## Behavioral evidence

The initial regression run compiled successfully: 24 new cases failed on the baseline and the pre-Apply validation retry control passed. `red-regressions.txt` records the failing test names. The corrected focused corpus passes 248 cases, excluding broker and storage integration categories.

- **#105:** first/middle/last replay failures through generated handlers and custom overrides discard the damaged aggregate, preserve the original exception, and reject replay, emission, audit, hydration and save. Array and non-array histories are covered. Fresh-instance recovery succeeds; pre-Apply validation remains retryable. Existing identity reservation controls remain passing.
- **#106:** processor list capacity is bounded by both batch size and remaining pass budget. Existing runner controls verify bounded reads, commits, partial batches and checkpoint continuation. The synchronous allocation probe below measures empty and one-event passes independently of timing.
- **#107:** single and batch scenarios drain 4,097 and 8,193 events, including mixed matches. Projection repeats resume committed checkpoints; separate reactor runs replay once each. Failure and caller cancellation within and between passes preserve committed progress and propagate the original failure/token.
- **#108:** conformance compares the original concrete payload and complete metadata on stream, offset, pattern and every issued-cursor read, including an audit event and the winning concurrent append. Corrupting wrappers prove rejection of payload/type changes, path-specific corruption and every metadata member. Timestamp offset is compared explicitly because timestamp equality alone ignores it.
- **#109:** public instance fields and publicly readable properties are compared recursively. Static members, indexers and inaccessible getters are excluded; mixed/nested mismatches retain member paths.

The extended `Portia.ProjectionStore.PackageConsumer` exercises the shipped packages with a fresh isolated NuGet cache and JSON reflection disabled: replay disposal/recovery, field assertions, 4,097-event draining, bounded manual processing and event-store conformance. CI also runs existing Linux NativeAOT consumers and real S3/Fitz integration, including the updated conformance suite. Backend and NativeAOT qualification require successful CI on the final PR commit and again on the merged commit; local fixture success alone does not establish these gates.

## Allocation evidence

Run the probe in Release:

```sh
dotnet run --project bench/Portia.Benchmarks --configuration Release -- --processor-pass-allocation-probe
```

For the baseline measurement, use the same probe with the two runner files restored from the baseline commit. `source-sha256.txt` identifies those runner sources and the probe; `environment.txt` records the .NET and host environment. Both CSV files are raw paired output, using two warm-up passes and 16 measured synchronous passes per case. The probe checks completion and processed event counts and measures thread-local allocation only.

With batch size 1,048,576 and pass budget 1, the baseline allocated approximately 8.39 MB per pass. After bounding capacity, allocations match the batch-size-1 control exactly: empty/one-event projector passes allocate 856/1,312 bytes and reactor passes allocate 1,944/2,480 bytes. The small-batch controls are unchanged. No latency or throughput improvement is claimed; this probe is independent of the later full processor performance baseline.

## Adversarial review

Review checked original exceptions and reservation cleanup, validation-before-mutation, handler and override failures, second-pass failures, between-pass cancellation, no-progress termination, per-run replay boundaries, original-versus-read metadata comparisons, concurrent winner bookkeeping, and filtering of static/indexed/inaccessible members. The metadata offset corruption control closes a subtle equality gap. Fixture conformance does not claim backend crash durability or transaction rollback of external application side effects.

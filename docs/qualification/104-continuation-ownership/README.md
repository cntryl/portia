# Continuation ownership qualification (#104)

A continuation now captures its originating request frame and behavior position. Before using
that position, it requires the originating frame to be current. The existing atomic position
state rejects repeated calls, closed behaviors, and completed requests. Delegates are never
rebound or pooled across invocations. All three pipeline shapes use the same ownership rule.

On baseline `733d895314da7511a93155562a71ca9a92175d92`, all twelve new ownership cases fail:
unused/used continuations invoked inside later requests and live continuations borrowed by nested
or concurrent requests, for unary, result, and stream dispatch. See `baseline-red.txt`. With the
patch, the 32-case pipeline corpus passes, including asynchronous use, scope isolation, partial
stream disposal, and inline/extended position single-winner controls (positions 30 and 31).

Local qualification: locked solution restore; Release build with zero warnings/errors;
solution formatting; 1,597 broker/storage-free tests passed with the existing queue-policy skip;
freshly packed HTTP consumer in an isolated cache passed, including an expired result continuation.
The source-mode HTTP host lacks its direct HTTP generator reference and fails endpoint interception;
package mode supplies that generator and passed. Linux NativeAOT and real storage/broker checks
are required in hosted final-head and post-merge CI; local macOS managed execution is no substitute.

Adversarial review checked stale unused and used delegates, borrowing while their owner is active,
ambient frame identity versus plan identity, behavior closure and invocation completion, nested
frame restoration, captured execution contexts, concurrent single-use races, and stream disposal.
No position-state mutation or public API change is required. New delegate captures allocate per
invoked behavior; that cost is accepted to enforce ownership rather than reuse mutable leases.

The existing dispatch benchmark was rerun as a diagnostic ShortRun on Apple M5/macOS with .NET SDK
10.0.400, runtime 10.0.11, and BenchmarkDotNet 0.15.8. Raw JSON/CSV and the generated summary are
included, with baseline commit and runtime-file hashes in `source.json`. This is not an optimization
adoption run, and startup overlapped local verification. It shows 320 B for one synchronous behavior
and 736 B for five. Do not infer timing improvement from these short, noisy measurements; the full
request lifecycle campaign remains #126.

# Hosted operational worker benchmark

The [direct journal baseline](operational-journal-baseline.md) measures individual Fitz operations.
The hosted benchmark measures actual Portia projector passes and operator status queries with
a synchronous Fitz projection, journal, and lease coordination. Each run creates a separate Fitz
realm for each row. Use a dedicated, disposable broker with local disk storage.

## Build and run

Use the same .NET 11 SDK, OS, hardware, and broker image digest for every run in a comparison.
Use a fresh broker data directory for each process. Record the container image digest, storage
mount type, and broker config along with the machine and SDK details; hold them constant. The
example uses Docker's local writable layer and disables Fitz authentication on a loopback port.

```sh
fitz_image=ghcr.io/cntryl/fitz@sha256:6d4f121fd772c17ef059c15f8653488ae8466ed30c6e5ef789b01ce65e395f30

docker run --rm -d --name portia-journal-benchmark -p 127.0.0.1:40939:4090 \
  -e FITZ_ADMIN_AUTH_MODE=open -e FITZ_AUTH_REQUIRED=false -e FITZ_HTTP_PORT=4090 \
  -e FITZ_STORAGE_MODE=local -e FITZ_STORAGE_PATH=/data/fitz -e FITZ_TCP_ENABLED=false \
  "$fitz_image"

dotnet build bench/Portia.Benchmarks -c Release
revision="$(git rev-parse HEAD)"
mkdir -p artifacts/operational-worker
cp bench/Portia.Benchmarks/bin/Release/net11.0/Portia.Benchmarks.deps.json \
  "artifacts/operational-worker/$revision.deps.json"
dotnet --info > "artifacts/operational-worker/$revision.dotnet.txt"

PORTIA_OPERATIONAL_WORKER=run \
FITZ_TEST_ENDPOINT=ws://127.0.0.1:40939/ws \
PORTIA_OPERATIONAL_WORKER_ARTIFACT="artifacts/operational-worker/$revision-01.json" \
PORTIA_OPERATIONAL_WORKER_COMMIT="$revision" \
PORTIA_OPERATIONAL_WORKER_SDK="$(dotnet --version)" \
PORTIA_OPERATIONAL_WORKER_BROKER="$fitz_image" \
PORTIA_OPERATIONAL_WORKER_MACHINE="CPU model, RAM, disk and host identifier" \
  dotnet bench/Portia.Benchmarks/bin/Release/net11.0/Portia.Benchmarks.dll

docker stop portia-journal-benchmark
```

For repeatable runs, use the repository launcher with a dedicated benchmark worktree and
a locally available copy of the pinned Fitz image. Build both variants first. The launcher
does not build during a smoke or measured process.

```sh
bash bench/operational-worker/run-process.sh prepare "$baseline_worktree" "$evidence/baseline-build"
bash bench/operational-worker/run-process.sh prepare "$candidate_worktree" "$evidence/candidate-build"
bash bench/operational-worker/run-process.sh smoke "$evidence/baseline-build" "$evidence/setup-check" "$machine"
# After an accepted host sample, repeat in the declared process order:
bash bench/operational-worker/run-process.sh run "$evidence/baseline-build" "$evidence/pair-01-baseline" "$machine" "$host_record"
```

Create the absolute evidence parent directory first. The launcher captures the tracked patch
and benchmark source overlay, SDK details, immutable build inventory, broker image and storage
configuration, launch timestamps, and raw result. It uses a fresh broker per process and
verifies the build inventory again after exit. Use the SHA-256 of each build's
`overlay-sha256.txt` as its overlay digest in the campaign manifest. Review the host record before
launch and monitor the host throughout each pair. The launcher archives that evidence and does
not decide whether the observed host conditions satisfy the policy.

For a setup and correctness smoke, use `PORTIA_OPERATIONAL_WORKER=smoke`. This runs one
workload with four retained receipts and two healthy events. A full run covers the cross product
of 4 and 128 retained receipts with 1 and 8 independent workloads. Each workload has its own
source and native projection route. It gets 20 healthy events, followed by one event while a
shared application dependency is unavailable. Workers exhaust their transient retry budgets with
the broker still available. The dependency reopens only after all workloads are blocked. An
operator then previews, approves, and queues exact repairs while status queries continue.

## Read the artifacts

Each row records phase elapsed time, native commit and status query latency samples, p50/p95/p99,
commit throughput, retry starts, and worker checkpoint and journal call counts. Publish to
commit latency starts just before the append. For the outage event it includes the outage and
the operator delay. Recovery elapsed time starts when approval and queueing begin and ends when
repaired projection and journal state have been verified. Outage retry starts are the repeated
failed attempts after the first failure. Polls that did not invoke a retry contribute to pass
counts.

Worker journal reads and compare-and-exchange calls are logical store operations. Native
checkpoint loads count explicit reads and native batches count opens that include a checkpoint
read. Per-pass averages include empty and blocked passes between observations. The journal byte
count is the exact Fitz JSON encoding of successful worker document writes, reconstructed
outside each measured phase. It excludes keys, indexing, protocol overhead, and separate
operator operations. Status queries use the application request bus, sample native progress and
journal evidence, and run every 50 ms per workload as capacity allows. The readiness query
before each phase and any cancelled final query are excluded from the latency distribution.

The benchmark verifies all normal commits, both the native checkpoint and native model during
the outage, every failed attempt's source and checkpoint, loss of the stale-owner write, and
the exact repair's committed receipt and unique durable attempt IDs. It measures application
dependency failure and recovery through the real broker. Network interruption, provider failover,
process restart and reactors are outside this fixture. Probe and counter overhead and broker
resource contention both contribute to the elapsed numbers. The results cannot establish which
part of the broker spent time without further profiling.

## Compare revisions

Use the same benchmark source at each candidate and baseline source revision. Build from separate
worktrees and preserve the exact harness patch hash for revisions that do not include the runner.
Archive the output directory, dependency manifest, and host/provisioning details from each run
using a different artifact filename. A stopped run may leave an `.incomplete` artifact with only
its finished rows.

Start with a repeated same revision comparison under an idle host. Before recording a comparison,
write down the host acceptance criteria and an acceptable regression margin for elapsed recovery,
commit throughput and tail latency. Run an equal number of independent baseline-first and
candidate-first pairs. Treat each process run as one independent observation. Take paired ratios
or differences per row and phase, and report intervals across pairs. Preserve rejected and
incomplete runs separately. Use a focused provider or host profile before attributing a
difference to serialization, checkpoints or retry scheduling. The direct journal baseline is a
separate control for operation cost and its output is not comparable with the hosted timings.

For the core reliability comparison, use `7fc296a962568c1fa3d40906e5ca9705e738addb` (before
the #302 changes) and `38886f1da27351b26774e8333dae015998c35f0e` (after #310, #313, #314
and #311). Both use Fitz client 4.3.0 with the same central dependency and build settings.
Copy the hosted benchmark's two `OperationalWorker*.cs` files into each worktree and add
the same worker-mode dispatch to each bench entry point. The baseline predates the direct
journal runner, so omit that dispatch there. In the baseline's `Portia.Fitz.csproj`, give
`Portia.Benchmarks` internal access to the exact Fitz serializer, as the candidate already
does. Save the overlay diff as part of the run provenance.

The later #302 closure at `ffee223df15865352896b6343783e744c89532ba` has additional status
changes and bumps Fitz to 4.3.1. A comparison that includes that revision measures both changes
unless the Fitz dependency is explicitly held constant and the resulting build is recorded.

### Analyze a campaign offline

Record an A/A pilot in its own manifest, using the same revision and overlay on both sides.
Use at least six independent pairs with equal numbers of baseline-first and candidate-first
pairs. Keep one campaign's accepted machine and environment fixed. Before recording A/B runs,
freeze the thresholds and references to the host acceptance evidence. Record process start
times when launching each process. Put the accepted pairs in launch order. Keep rejected runs,
their host records, overlay diffs, and build manifests alongside this manifest.

For example, this shows one entry from the manifest. Add the other pairs in the same shape.
The margins shown are examples. Choose them before the campaign, using the pilot as evidence.

```json
{
  "policy": {
    "declaredUtc": "2026-01-01T00:00:00Z",
    "baselineRevision": "7fc296a962568c1fa3d40906e5ca9705e738addb",
    "candidateRevision": "38886f1da27351b26774e8333dae015998c35f0e",
    "harnessRevision": "bd09deb446c28c5dc2c5f792ec350efeda931353",
    "baselineOverlaySha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "candidateOverlaySha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "hostAcceptance": "path to frozen host policy",
    "marginsPercent": { "throughput": 5, "tailLatency": 10, "recoveryTime": 5 }
  },
  "pairs": [
    {
      "first": "baseline",
      "hostAccepted": true,
      "hostEvidence": "host/pair-01.json",
      "baseline": { "artifact": "baseline-01.json", "startedUtc": "2026-01-01T01:00:00Z" },
      "candidate": { "artifact": "candidate-01.json", "startedUtc": "2026-01-01T01:05:00Z" }
    }
  ]
}
```

Replace the example overlay identifiers with the actual hashes. Artifact paths
are relative to the manifest. The analyzer records them and their SHA-256 hashes in the result.
Review the referenced host evidence and build provenance before admitting a pair. The analyzer
checks run order, policy timestamps, environment, matrix coverage, sample counts and distinct
Fitz realms. Host acceptance and overlay hashes are recorded inputs to that review.

```sh
python3 bench/operational-worker/compare.py artifacts/operational-worker/campaign.json \
  > artifacts/operational-worker/comparison.json
```

The result computes raw-sample p95/p99 for normal publish-to-commit and for each phase's
status queries. It also measures normal and recovery throughput and recovery elapsed time.
Each process contributes one summary per metric and cell. Positive paired deterioration
means slower latency or recovery, or lower throughput. For each median, the analyzer reports
a two-sided sign-test interval with at least 95% coverage, treating pairs as independent.
The interval is per metric and cell. A whole interval at or below the margin is
`within_margin`, a whole interval above it is `exceeds_margin`, and an interval crossing it
is `inconclusive`. At six pairs the interval covers the entire observed range. Collect more
pairs when noise makes the result inconclusive. Use the raw checkpoint, journal, byte and retry
counts to investigate differences after the paired comparison.

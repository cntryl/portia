# Migrating to Portia 0.8.0

Portia 0.8.0 upgrades Fitz .NET from 1.4.2 to 2.0.0. Review the broker cutover and
KV durability changes before upgrading an existing deployment.

## Fitz 2.0 client and 0.2.0 broker

The updated `Cntryl.Portia.Fitz` uses the .NET Fitz 2.0.0 client and requires a compatible
Fitz 0.2.0 broker. KV persistence moved from BEGIN to an explicit choice at COMMIT. Portia now
defaults checkpoint, projection-batch and tenant-directory snapshot commits to `KvDurability.Buffered`;
previous versions selected `Sync`. Event batches continue to use `StreamCommitMode.Sync`.
The old and new KV wire contracts are
incompatible, so upgrade the broker and clients together during a maintenance cutover. Stop
application traffic, prepare the compatible broker, and wait for readiness before reconnecting
the upgraded application. See the [Fitz 0.2.0 release](https://github.com/cntryl/fitz/releases/tag/v0.2.0)
and [explicit persistence migration](https://github.com/cntryl/fitz/blob/v0.2.0/docs/operations/migration-guide.md#breaking-change-explicit-kv-and-stream-commit-persistence).

The new broker cannot open an older broker's store. Export application data through the old
broker and import or replay it into a fresh store, with application-specific migration and
reconciliation for queues, schedules, expiration metadata and writes made during migration.
Preserve the original broker and store for rollback; replacing the image against the existing
storage path is not an upgrade procedure. The upstream [migration guide](https://github.com/cntryl/fitz/blob/v0.2.0/docs/operations/migration-guide.md)
and [migration rehearsal](https://github.com/cntryl/fitz/blob/v0.2.0/docs/operations/release-0.2.0.md#migration-rehearsal)
describe the required boundaries and the limited scope of the supplied logical migration fixture.

## Buffered KV state and synchronous event streams

The event stream remains the authoritative persistence boundary. KV projection data and its
checkpoint still commit in one atomic transaction; buffering changes when persistence completes,
not whether the batch becomes visible together. A broker crash can lose recent buffered KV
commits, so projections and tenant snapshots may replay more of the durable event history.
Rewound reactor checkpoints can repeat external effects; reactors must remain safe to replay.

Applications can opt into `KvDurability.Sync` per store through the durability constructor.
For reactor registration, use `fitz.UseKvCheckpoints(route, KvDurability.Sync)`; the existing
one-argument overload now selects `Buffered`. A derived projection repository can call
`base(kv, route, componentName, KvDurability.Sync)`, and tenant snapshots can use
`new FitzKvTenantDirectorySnapshotStore(kv, route, KvDurability.Sync)`.

## Fitz queue acceptance and crash recovery

Fitz 0.2.0 queues provide reservations, acknowledgment and redelivery, with best-effort persistence.
Queue writes skip the WAL and persist in the background. Enqueue and acknowledgment responses
do not guarantee recovery after a broker crash: unpersisted messages and acknowledgments can be
lost, redelivery can duplicate effects, and unpersisted message IDs can be reused. The configured
flush interval is a target rather than a durability deadline. See the upstream
[queue persistence contract](https://github.com/cntryl/fitz/blob/v0.2.0/docs/operations/migration-guide.md#queue-persistence-is-always-best-effort).

Applications that require crash-safe work admission need a durable application intent and a
recovery mechanism that regenerates missing queued work. Consumers must tolerate duplicates.
Portia's HTTP `Prefer: respond-async` receipt confirms enqueue acceptance; it does not establish
durable process admission or completion. Other queue providers retain their own persistence
contracts.

## In-memory RPC test consumers

The updated `InMemoryRpcClient` test double returns Fitz's `RpcCall` from its public `CallAsync`
methods, including the timeout overload. Calls remain lazy and can still be consumed with
`await foreach`; the handle also exposes `Cancellation`. Budgets must be between zero and one
day, and cancellation ends the caller's wait even when a worker ignores its token. Update code
that binds to the previous concrete return signature and recompile test consumers.

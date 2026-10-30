# Upgrading to Fitz .NET 3

The unreleased Portia packages use `Cntryl.Fitz.*` 3.0.0 and require Fitz server 0.3.0.
The repository pins that broker in integration, example, benchmark, and crash-durability fixtures.
The .NET client package [release notes](https://github.com/cntryl/fitz-dotnet/blob/f02e3059b7d7998fb0b2611d546e8e68f33864ae/CHANGELOG.md)
and broker [migration guide](https://github.com/cntryl/fitz/blob/v0.3.0/docs/operations/migration-guide.md)
describe the breaking contracts.

Upgrade applications and the broker during one maintenance cutover. Fitz 0.3.0 uses wire format 2
without negotiation with older clients. Stop traffic and consumers before activating the new
broker and clients together. Rollback also requires matching broker and client versions.

Fitz 0.3.0 embeds Midge 0.5.0, whose database and cloud metadata formats cannot open the previous
store in place. Preserve a verified copy of the old store and matching broker for rollback, then
export with the old broker and import or replay into a fresh store. Fitz does not supply a general
export/import tool. Replayed streams receive new offsets and timestamps, so reconcile Portia
checkpoints and rebuild derived data against the migrated event history. Include schedules,
expiration metadata, queue recovery, and writes accepted during cutover in the application migration.
Qualify restart and recovery before restoring traffic. Do not point the new image at the old store.

Portia continues to select persistence explicitly at KV and Stream COMMIT. Event batches use
`StreamCommitMode.Sync`; the existing per-store `KvDurability` choices remain available.
Applications implementing Fitz's `INoticeClient` must add `PublishWithAckAsync`. Portia's notice
sender retains its existing unacknowledged publish behavior; callers requiring a broker response
can use the native client's acknowledged API.

## Queue acceptance and crash recovery

Fitz 0.3.0 queue persistence remains best effort. Enqueue and acknowledgment responses do not
guarantee recovery after a broker crash. Unpersisted work or acknowledgments can be lost, and
redelivery can duplicate effects. Required work needs durable application intent and a recovery
mechanism that regenerates missing deliveries; consumers must tolerate duplicates. See the
[upstream queue contract](https://github.com/cntryl/fitz/blob/v0.3.0/docs/operations/migration-guide.md#queue-persistence-is-always-best-effort).

Portia's asynchronous HTTP receipt confirms enqueue acceptance. It does not establish durable
process admission or completion. Authoritative business outcomes still determine completion,
including explicit rejection, compensation, or intervention when a participant refuses work.

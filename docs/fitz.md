# Fitz integration

`Cntryl.Portia.Fitz` supplies event storage, KV-backed projection and checkpoint stores, and
request transports. Use the client versions declared in [Directory.Packages.props](../Directory.Packages.props)
with a compatible Wire 3 broker. Local integration, example, benchmark, and crash-durability
fixtures use `ghcr.io/cntryl/fitz:latest`.

See [application setup](application-setup.md) for registration and worker hosting.
Broker storage and protocol upgrades follow the [broker migration guide](https://github.com/cntryl/fitz/blob/main/docs/operations/migration-guide.md).

## Events and KV durability

Event batches commit with `StreamCommitMode.Sync`. `FitzKvProjectionStore`, `UseKvCheckpoints`, and
`UseKvTenantSnapshots` default to `KvDurability.Buffered` and allow explicit `KvDurability.Sync`.
`UseOperations` uses synchronous commits for its operational journal and reactor checkpoints;
see [processor operations](operations.md).

Buffered projection commits keep model writes and their checkpoint atomic, but recent buffered KV
state can be lost after a broker crash. Rebuild derived data from events and make reactor effects
safe to replay.
See [projectors and reactors](projectors-and-reactors.md#fitz-kv-persistence) for repository wiring.

## Queue publication options

Use `EnqueueWithOptionsAsync` when a request needs an initial delay or per-message lifetime:

```csharp
await publisher.EnqueueWithOptionsAsync(command, routeValues, actorToken,
    new RequestQueueOptions(Delay: TimeSpan.FromSeconds(30), TtlHours: 24), ct);
```

`Delay` is a nonnegative whole-second duration. `TtlHours` is a positive `u32` count of hours;
omitting it leaves the broker's configured message lifetime in effect. Portia sets no per-message
TTL by default. The lifetime starts at publication and includes the initial delay. Reservation,
NACK, and redelivery do not restart it. The explicit-metadata and parent-context overloads preserve
the request's existing logical identity and causal chain.

The provider must support requested options; it cannot silently drop a nondefault delay or TTL.
Fitz negotiates the TTL capability with the broker. Native broker message and DLQ retention
configuration remain broker-owned.

## Retry failed deliveries with NACK

When Portia abandons incomplete queue processing, Fitz uses native NACK to release the live
reservation with a visibility delay. Configure the delay through ordinary queue runner options:

```csharp
services.Configure<QueueRunnerOptions>(options =>
    options.RetryDelay = TimeSpan.FromSeconds(10));
```

`RetryDelay` must be nonnegative and expressed in whole seconds. Null uses the consumer's configured
visibility timeout; zero makes the message eligible for immediate redelivery. With the default
null delay, unsupported NACK falls back to reservation expiry. An explicitly selected delay
requires provider support and fails when it cannot be honored. Other NACK failures remain
processing failures. Lost reservations and failed acknowledgments are not NACKed.

A successful NACK means the delivery was released for broker-owned handling. It does not record
a business rejection or establish completion. Persist an authoritative business outcome and return
`Result.Success` once that outcome's required work is complete; failed processing remains retryable.

## Queue retry limits

The broker leaves reservation retries unlimited by default. Set `FITZ_QUEUE_MAX_ATTEMPTS` on the
broker to a positive `u32` value (1 through 4294967295) to configure its reservation limit and
best-effort dead-letter behavior. Invalid values stop the broker during startup. The setting
applies to every queue and is independent of Portia's `QueueRunnerOptions.TerminalAttempt`.

The .NET reserved-item contract exposes no durable attempt count. Fitz deliveries report
`QueueInvocation.Attempt` as `0` and `SupportsDurableAttempts` as `false`; Portia rejects a positive
`TerminalAttempt` for Fitz workers.
Use the broker setting to select broker retry policy; Portia adds no duplicate attempt counter.
See the [broker queue contract](https://github.com/cntryl/fitz/blob/main/docs/clients/spec/queue-rpc.md#retry-limit-and-dead-letters).

## Queue acceptance and crash recovery

Fitz queue persistence is best effort. Enqueue and acknowledgment responses do not
guarantee recovery after a broker crash. Unpersisted work or acknowledgments can be lost, and
redelivery can duplicate effects. Required work needs durable application intent and a recovery
mechanism that regenerates missing deliveries; consumers must tolerate duplicates.

Portia's asynchronous HTTP receipt confirms enqueue acceptance. Authoritative business outcomes
still determine completion, including explicit rejection, compensation, or intervention when a
participant refuses work. See the [broker persistence contract](https://github.com/cntryl/fitz/blob/main/docs/clients/spec/queue-rpc.md#queue-domain-work-delivery-with-best-effort-persistence).

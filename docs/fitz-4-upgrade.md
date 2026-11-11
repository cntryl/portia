# Upgrading to Fitz .NET 4

Portia 0.11.0 targets `Cntryl.Fitz.*` 4.1.0 and requires Fitz server 0.4.0 / Wire 3.
Integration, example, benchmark, and crash-durability fixtures pin the published 0.4.0 broker image.
See the [.NET client release notes](https://github.com/cntryl/fitz-dotnet/blob/v4.1.0/CHANGELOG.md)
and [broker migration guide](https://github.com/cntryl/fitz/blob/v0.4.0/docs/operations/migration-guide.md#040-coordinated-wire-3-broker-and-sdk-cutover).

## Coordinated broker and client cutover

When moving from Wire 1 or Wire 2, upgrade the broker and every connected SDK together.
Fitz .NET 4.0 to 4.1 retains the same Fitz 0.4.0 / Wire 3 broker contract.
Fitz 0.4.0 accepts only Wire 3 and cannot
serve Wire 1 or Wire 2 clients. Stop traffic and consumers before replacing the broker and
clients, and roll back both together. Reconnecting creates a fresh ephemeral session; clients
reauthenticate and recreate subscriptions and worker registrations.

Wire 3 adds bounded extension envelopes and capability negotiation while retaining the domain
core payload semantics. Portia exposes the client's capability-gated queue TTL, initial delay,
and native NACK with retry delay through the request publisher and queue runner.

Fitz 0.4.0 adds no Fitz domain-data format change relative to 0.3.0. Its embedded Midge 0.5.2
retains the Midge 0.5.0 and 0.5.1 persisted formats. Preserve a verified backup before cutover
and qualify restart and rollback against the matching broker and data. An upgrade from an older
storage generation must also follow the [previous storage migration](fitz-3-upgrade.md).

Portia still commits event batches with `StreamCommitMode.Sync`; the explicit per-store
`KvDurability` choices remain available.

## Queue publication options

Existing `EnqueueAsync` calls keep their current behavior. Use `EnqueueWithOptionsAsync` when a
request needs an initial delay or per-message lifetime:

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

The .NET reserved-item contract still exposes no durable attempt count. Portia therefore keeps
`QueueInvocation.Attempt` unavailable and rejects a positive `TerminalAttempt` for Fitz workers.
Use the broker setting to select broker retry policy; Portia adds no duplicate attempt counter.
See the [broker queue contract](https://github.com/cntryl/fitz/blob/v0.4.0/docs/clients/spec/queue-rpc.md#retry-limit-and-dead-letters).

## Queue acceptance and crash recovery

Fitz 0.4.0 queue persistence remains best effort. Enqueue and acknowledgment responses do not
guarantee recovery after a broker crash. Unpersisted work or acknowledgments can be lost, and
redelivery can duplicate effects. Required work needs durable application intent and a recovery
mechanism that regenerates missing deliveries; consumers must tolerate duplicates.

Portia's asynchronous HTTP receipt confirms enqueue acceptance. Authoritative business outcomes
still determine completion, including explicit rejection, compensation, or intervention when a
participant refuses work. See the [broker persistence contract](https://github.com/cntryl/fitz/blob/v0.4.0/docs/operations/migration-guide.md#queue-persistence-is-always-best-effort).

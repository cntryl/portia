# Requests, reactions, and event attribution

The normal event-sourced handler has three steps:

```csharp
var account = await repository.HydrateAsync(new Account(context.Request.Id), ct);
account.Deposit(context.Request.Amount);
await repository.SaveAsync(account, context, ct);
```

Portia supplies context to handlers and authorizers. Applications construct aggregates
normally, including any constructor dependencies. No aggregate registration is required.
Saves require explicit context; they cannot accidentally omit attribution.

## Shared execution model

`IRequestContext<TRequest>` and `IReactorContext<TEvent>` both implement
`IExecutionContext`. The common properties describe the operation producing effects:

| Property | Meaning |
| --- | --- |
| `Actor` | Principal executing this operation; returned as an isolated snapshot |
| `ExecutionId` | New UUID v4 for this execution attempt |
| `CorrelationId` | Identity shared by related work |
| `CauseId` | Current request ID or triggering event ID; becomes new events' causation ID |
| `StartedAt` | Receiver's UTC start time, using its registered `TimeProvider` |

Request context additionally provides `Request`, `RequestId`, `CausationId`, and
`Invocation`. `CausationId` identifies the operation that created the request;
`CauseId` identifies the request itself. A root request uses its own ID as its correlation
ID. An HTTP trace identifier is an ingress fact, not a trusted Portia correlation ID.

Authorization and handling see the same execution identity and start time. Streams
retain that execution throughout enumeration. Cancellation remains an explicit
`CancellationToken`, including transport disconnect and lost queue reservation.

## Transport facts

| Invocation | Facts populated by Portia |
| --- | --- |
| `DirectInvocation` | In-process dispatch |
| `HttpInvocation` | Method, path including path base, matched route pattern, ASP.NET request identifier |
| `RpcInvocation` | Actual received RPC route |
| `QueueInvocation` | Actual queue route and transport-reported attempt |
| `NoticeInvocation` | Actual received notice route |
| `ScheduleInvocation` | Actual received schedule route |

HTTP paths exclude query strings. Context does not carry raw credentials, headers,
service providers, or acknowledgement handles. A wildcard subscription selector is
not substituted for the actual route received. Custom transports can define another
`RequestInvocation` record and must supply execution context through `DispatchAsync`.

Fitz 0.1.2 does not expose broker message, call, reservation, or schedule occurrence
IDs through these delivery objects. Portia does not invent them. The isolated broker
returned attempt `1` on a queue redelivery; treat `Attempt` as transport-reported
information, not an application idempotency key or a guaranteed monotonic counter.

## Child requests and credentials

An in-process child inherits actor and correlation while getting new request and
execution IDs:

```csharp
await bus.SendAsync(new RecalculateAccount(id), context, ct);
```

Out-of-process child operations explicitly carry context and credentials separately:

```csharp
await publisher.EnqueueAsync(command, routeValues, actorToken, context, ct);
await remote.SendAsync(command, routeValues, actorToken, context, ct);
```

Notice publishing and scheduling have equivalent parent-context overloads. The
receiver validates the token at execution time. Passing context never converts a
principal into a credential and never bypasses receiver authorization. For system
requests crossing a transport, supply the application's system credential.

For an intentional resend of the same logical request, create `RequestMetadata`
once and reuse it with the metadata overload. The ordinary root sender overloads
create a new logical request on each call. HTTP `Prefer: respond-async` preserves
the accepted request's logical metadata when publishing it to the queue.

The JSON envelope is version 1 and requires valid request metadata. Missing metadata,
empty identities, and unsupported versions are rejected. Receiver execution IDs,
start times, and invocation facts are never accepted from the envelope.

Queue redeliveries reuse the envelope's request ID but create a fresh execution ID.
A recurring schedule is a template: each observed firing gets a new request ID,
inherits the template's correlation ID, and names the template request as its cause.
Without a broker occurrence ID, duplicate scheduled deliveries cannot be recognized
as the same occurrence. Applications needing that guarantee must supply their own
business occurrence key.

## Reactions use system identities

A reaction's `Source` contains the triggering event and its stream offsets; `Ev`
provides the typed business event. The runner inherits correlation from that event,
uses its event ID as the cause, and creates an independent system execution.
The original event's actor is attribution only; it never authorizes the reaction.

The default principal is `RequestActor.System`. To distinguish system workloads,
register an `IReactorPrincipalProvider` through ordinary DI:

```csharp
public sealed class ReactorPrincipals : IReactorPrincipalProvider
{
    public ClaimsPrincipal GetPrincipal(Reactor reactor)
        => RequestActor.CreateSystem($"accounts:{reactor.Name}", "my-application");
}

services.AddSingleton<IReactorPrincipalProvider, ReactorPrincipals>();
```

The runner rejects end-user principals. A reactor saves using its supplied context
exactly as a request handler does: `await repository.SaveAsync(account, context, ct)`.

## Saving and retrying

Save stamps correlation, causation, execution identity, and stable actor subject and
issuer onto every pending event or audit. It preserves the event ID, aggregate ID,
version, occurrence time, and audit classification assigned when the event was raised.
Authenticated actors require a `NameIdentifier` or `sub` claim; anonymous attribution
is explicit. Tokens and claims are never stored in event metadata.

A save contains raised events or audits, never both. Portia validates the entire
batch before stamping it. The first append attempt freezes the batch and attribution:
retry the same aggregate with the same context. Additional emissions are rejected
until that save succeeds. Cancellation before an append attempt does not freeze the
batch. Changing the execution context of an unconfirmed save is rejected.

This is not exactly-once command execution. A redelivered command or a reactor
replayed after checkpoint failure can execute application logic again. Use an explicit
business idempotency key where duplicate effects matter, and persist its receipt with
the affected state. On an optimistic concurrency conflict, reload a fresh aggregate
and decide whether the business operation is still valid; Portia does not rerun it.

Use UUID v4 for fresh identities and UUID v5 for deterministic business identities.
Event order comes from aggregate versions and stream positions, not UUID timestamps.

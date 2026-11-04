# Migrating to Portia 0.10.0

This guide describes the stable 0.10.0 release. It includes processor contract
changes previously prepared under the unpublished 0.9.3 source version. Upgrade the Portia package
family together; publishing and deployment remain separate release steps.

## Configure operator access

Set `PORTIA_SYSTEM_ADMIN_ROLE` through application configuration or the environment to the role
carried by authenticated system operators. Missing or blank configuration denies access. Portia
requires that role on an authenticated identity, honoring its role claim type, for processor
status, direct operations calls, administrative MCP discovery, and administrative HTTP/MCP actions.

```json
{
  "PORTIA_SYSTEM_ADMIN_ROLE": "portia-operator"
}
```

Processor status requires no application-defined authorizer. An existing `IOperationalAuthorizer`
must additionally permit `OperationalAccess.Read` for the requested tenant or global scope.
Durable diagnosis and repair through `IPortiaOperations` continue to require an explicit
`IOperationalAuthorizer` with the appropriate `Read`, `Preview`, `Approve`, or `Execute` permission.
The configured operator role alone grants no repair approval or execution permission.

Normal `AddFitz(...).UseOperations(...)` composition passes the host's `IConfiguration` to the
operational API. Applications constructing it manually should use the configuration-aware factory
when the role comes from application configuration:

```csharp
var operations = PortiaOperations.Create(configuration, store, options, topology, authorizer);
```

The existing constructor remains available and uses the environment role setting. Preserve the
separate operator-approval credential and exact-plan authorization when migrating repair tooling;
see [processor operations](operations.md).

## Inspect each projector or reactor with one call

Each hosted component exposes one status RPC while its worker owns the workload. Shared Fitz
application setup supplies `IProcessorStatusClient`; workers activate endpoints through `AddWorkers()`.
Pass the registered component name, its tenant or null for a global workload, and operator credentials:

```csharp
var result = await status.GetAsync("AccountBalanceProjector", tenant, actorToken, ct);
```

The result reports the authoritative native checkpoint, available source-progress measurements,
and applicable failure evidence. Unknown measurements remain null; bounded probes can report a
lower bound instead of an exact backlog. Inspection does not run the processor or advance its
checkpoint. See [processor inspection](projectors-and-reactors.md#operator-inspection-of-a-projector-or-reactor).

## Return processor outcomes

Framework component, tenant, stream, and generation identities now reject NUL and malformed UTF-16
before routing or persistence. Existing valid checkpoint keys keep their byte representation; this
validation change requires no key migration for ordinary identities.

Manual reactor runs now reject `ProjectionRunOptions.RebuildId`, matching hosted registration.
Projection rebuilds must isolate application-owned model data as well as checkpoints; the
[Fitz KV example](projectors-and-reactors.md#fitz-kv-persistence) passes the generation to its keys.

Applications upgrading from published 0.9.2 must update projector and reactor handlers, including
batch handlers and manual overrides, to return `ValueTask<Result>`. Projector handlers receive
`IProjectorContext<TEvent>`: read the event through `Trigger`, its source through `Source`, and the
shared batch identity through `Batch`.

`SendReactionAsync` returns the command result. Return or explicitly propagate it before further
work; awaiting it and unconditionally returning success can advance a checkpoint after failed work.
Declared failures leave processing incomplete. Expected business rejection still requires its
recorded domain outcome, with explicit rejection, compensation, or intervention where appropriate.

Processor failures surface as `ProcessorFailureException`, carrying stage, source range, last
confirmed checkpoint, and a declared `Error` or original `InnerException`. Update tests and exception
handling accordingly. The earlier [processor migration detail](migration-0.9.3.md) includes examples.

## Check legacy effect identities

`CreateEffectId` keeps every accepted identity's exact legacy UUID derivation; it does not introduce
a new key version or rewrite stored IDs. It now fails when the legacy field boundaries are ambiguous:

- A reactor name contains `:<canonical lowercase nonempty UUID>` after a nonblank prefix, followed
  by another `:` or the end of the name. This checks any UUID, not just the current source event.
- A bound workload name contains the literal `, Tenant = `.
- A bound tenant value contains the literal ` } }:`.
- An effect name contains malformed UTF-16, which would otherwise be replaced during UTF-8 hashing.

Ordinary names with colons and valid Unicode keep their IDs. A UUID at the start of a reactor name
does not make its prefix a different valid reactor name and remains supported.

Before upgrading a workload with an affected identity, stop its owners and reconcile the recorded
effects against the target's idempotency records. Choose an unambiguous identity or an explicit
application-owned idempotency scheme, and migrate the target records as part of that decision.
Do not merely rename, reset checkpoints, or replay with new keys: the old IDs may already represent
completed effects, and two formerly colliding effects may need separate business resolution.
Portia performs no automatic migration for these unusual identities.

## Upgrade dependencies and the broker together

Portia 0.10.0 uses Fitz .NET 4.0.0 and requires Fitz 0.4.0 / Wire 3. Replace the broker and connected
SDKs together, and pair their rollback. Wire 1 and Wire 2 clients cannot connect to that broker.
Follow the [Fitz 4 upgrade guide](fitz-4-upgrade.md), including its stored-data compatibility and
best-effort queue-persistence boundaries.

Fitz now exposes `FITZ_QUEUE_MAX_ATTEMPTS` as a broker setting, with unlimited retries by default.
The .NET reserved-item contract still has no durable attempt count, so Portia continues to reject
a positive `QueueRunnerOptions.TerminalAttempt` for Fitz workers.

Existing `EnqueueAsync` calls need no change. To select publication controls, use
`EnqueueWithOptionsAsync` with `RequestQueueOptions(Delay: ..., TtlHours: ...)`. Delay is expressed
in whole seconds and TTL in positive hours; TTL starts at publication and includes initial delay.
Portia adds no per-message TTL by default. Custom providers must explicitly support requested
options rather than silently discarding them.

Abandoned Fitz deliveries now use native NACK. Configure `QueueRunnerOptions.RetryDelay` through
ordinary options registration when needed: null uses the consumer visibility timeout, zero permits
immediate redelivery, and a positive whole-second duration defers it. The default delay falls back
to reservation expiry when NACK is unsupported; an explicit delay must be supported. NACK releases
unfinished processing; it does not replace an authoritative business rejection or make queue
acceptance durable. Broker retry limits and DLQ remain broker-owned.

Microsoft Extensions and ASP.NET Core dependencies remain at 10.0.14, Microsoft.OpenApi stays on
the compatible 2.13.0 line, ModelContextProtocol packages move to 2.3.1, and JsonSchema.Net moves to
9.6.0. Update conflicting direct references instead of suppressing package downgrade diagnostics.
Repository development uses central package versions and ordinary `dotnet restore`.

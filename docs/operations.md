# Processor operations

Portia operations explain processor failures from durable execution evidence, preview a recovery,
record an operator's approval, queue execution for the owning worker, and report processing progress.
Applications own business invariants, participant outcomes, compensation, and reconciliation.
A committed processor checkpoint does not establish business consistency or business success.

## Activation and cutover

Stop all legacy owners of the affected workloads before enabling operations. Keep the same checkpoint
base route: operations preserves the existing per-component/per-realm resource derivation and checkpoint
keys, including legacy checkpoint encodings. Enabling both checkpoint registrations is rejected.
Every reactor must use the registered operational checkpoint store. Projectors retain their native
application repository and its transaction for both model data and progress.

```csharp
services.AddSingleton<IOperationalAuthorizer, ApplicationOperationsPolicy>();
services.AddPortia()
    .AddFitz(configuration.GetSection("Fitz"), fitz =>
        fitz.UseOperations("kv://application/portia/checkpoints", options =>
        {
            options.QuiescedWorkerCutover = true;
        }))
    // Register the application's requests, projectors, reactors, and workers as usual.
    .AddAdminMcpHttp();

app.MapPortiaAdminMcp();                 // /admin/mcp
app.MapPortiaRepairApprovals();          // /admin/repairs/{planId}/approve and /revoke
```

`AddAdminMcpHttp`, `MapPortiaAdminMcp`, and `MapPortiaRepairApprovals` are in the optional
`Cntryl.Portia.Admin.Mcp.AspNetCore` package. Apply the host's authentication, authorization,
allowed-host, and endpoint conventions. Both mapped surfaces require authentication. Operational
authorization defaults to deny until the host supplies `IOperationalAuthorizer`.

`OperationalAccess.Approve` must be absent from the operational agent's delegated credential.
Use a separate authenticated operator credential and policy for approval and revocation. Endpoint
authorization supplements the operational API's policy checks; calling the API directly cannot
bypass approval. Mutations require a stable authenticated subject (`ClaimTypes.NameIdentifier` or `sub`) and issuer.
Approval and execution records use Portia's `ActorAttribution` without copying claims or credentials.

## Diagnosis and recovery

All `IPortiaOperations` calls carry an actor and an exact `OperationalScope`. `Global` includes only
global workloads; use `OperationalScope.ForTenant(...)` for one tenant. An `OperationalTarget` also
identifies the registered processor, canonical bound stream pattern, and optional projection generation.
Opaque checkpoints must be supplied and compared unchanged.

1. Describe registrations and inspect scoped health and issues. Describing topology does not resolve
   application handlers, processors, or their constructor dependencies. Generated subscriptions use
   the dispatch generator's handler selection order, including derived events and batch grouping.
   Manual dispatch, overrides, and opaque application dependencies have unknown coverage.
2. Trace a recorded attempt. Its source references, actual generated invocations, local request
   identities, execution IDs, result classifications, and available committed event references are
   processing evidence. Traces exclude business payloads, claims, credentials, exception messages,
   and stack traces. Additional trace detail is bounded and reports truncation. Remote effects and
   dependencies outside the observed execution remain unknown.
3. Preview an immutable repair plan. Preview invokes no application handler. The plan binds the
   failed attempt and failure revision, ordered source set, exact checkpoint, processing settings,
   target scope, and current owner session. Plans expire after five minutes by default.
4. Approve that exact plan through the operational API or POST
   `/admin/repairs/{planId}/approve`, with the target as a JSON body. POST `/revoke` revokes an
   unclaimed approval. Approval expires with the plan.
5. Execute the approved plan. This reauthorizes and queues one execution; duplicates return the
   same repair. Only the owning worker can claim it. The worker rechecks approval, expiry, ownership,
   failure revision, source identities, settings, and checkpoint before invoking handlers.
6. Verify the repair's queued, running, committed, failed, invalidated, or indeterminate status.
   Processing verification does not infer a business outcome.

Retries replay the complete uncommitted unit with its original source events and stable effect IDs,
new execution IDs, the configured system reactor principal, normal downstream command authorization,
and bounded retry policy. Approval grants the recovery action. It does not grant business permissions.
A later terminal failure produces a new durable block and requires a new plan.

## Failure and durability semantics

With operations enabled, terminal processor failures durably block that workload. Other workloads and
administration remain available. Coordinator and bootstrap failures retain their host-failure behavior.
Required journal outages pause handler invocation rather than running without durable start evidence.
Completed failures and retry counts survive ownership changes. Pending plans become invalid when
ownership changes, and interrupted attempts remain visible when automatic crash replay succeeds.

The Fitz journal uses synchronous commits and short resource transactions. Attempt starts precede
application invocation. Reactor completion, the original checkpoint key, and the repair receipt commit
atomically. A lost acknowledgement is resolved from that receipt before any replay. No operational
transaction remains open during application handlers.

Projectors commit data and checkpoints in their repository's native transaction. Operational
confirmation follows disposal of that transaction. After confirmation loss the worker reloads native
progress; it never rewinds progress to satisfy an old repair plan. Unresolved historical evidence is
indeterminate. A confirmed commit followed by cleanup failure permits resume without reapplying the
committed unit.

Completed records default to thirty-day retention, configurable through `OperationalOptions`.
Unresolved failures and active repairs remain retained. Inspection exposes the retained-history
boundary. Running counts describe durable attempt records; they do not independently prove that an
owner process is still alive. Source payloads remain in the application event store.

## Administrative MCP isolation

The administrative tools are `describe_application`, `get_system_health`, `list_issues`,
`inspect_processor`, `trace_causality`, `preview_repair`, `execute_repair`, and `verify_repair`.
There is no approval tool. Every call supplies an exact scope; traces also require a recorded attempt.

V1 requires stateless Streamable HTTP. The SDK's per-request options callback selects the surface from
trusted endpoint metadata. Administrative requests replace application tools, handlers, filters,
resources, prompts, raw request handlers, and capabilities. Application fallback handlers cannot be
reached from the administrative endpoint. Shared stateful HTTP configuration is rejected.

## Executable qualification

Broker-free tests cover policy enforcement, exact approval, invalidation, duplicate execution,
atomic receipts, lost acknowledgements, interruption, changed sources, journal outages, projector
authority, generated topology, and cross-surface MCP calls.

`OperationalJourneyIntegrationTests` uses real Fitz effects, restarts the owning worker while blocked,
approves and verifies recovery, checks independent workload progress, and projects committed effects
into Cassie. Run it with the normal Fitz and Cassie integration environment.

```shell
dotnet test test/Portia.Tests/Portia.Tests.csproj -c Release --filter "Category=DurabilityIntegration"
```

The durability test owns an isolated Fitz container and named persistent volume, uses an executable
test worker to reach controlled interruption points, kills that process, recreates the broker, and
asserts preserved failures, interrupted attempts, source identities, effect deduplication, and progress.
It requires Docker and cleans up its own resources. The ordinary tmpfs Fitz fixture and memory Cassie
fixture qualify their respective integration behavior, not persistence across server recreation.

Operations does not infer business obligations, provide business consistency verdicts, compensate
effects automatically, skip or quarantine events, administer projection promotion, or simulate business
workflows. See [validation authority](validation-authority.md) for application-owned outcome validation.

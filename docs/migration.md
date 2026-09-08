# Current API contract

Portia is being designed as a greenfield framework. The current API has no legacy
aggregate factories, ID-based repository loading, unattributed repository saves,
or context-dropping adapter fallbacks.

- Configure the application once with `AddPortia`; activate its declared workers
  only in the worker deployment with `AddWorkers()`.
- Select handlers and authorizers independently with `AddRequestHandler<T>()` and
  `AddRequestAuthorizer<T>(AuthorizationStage)`. Sender-only contracts are inferred from
  strongly typed dispatch calls. Portia.Generators replaces the stable generic
  calls with typed descriptors and reports an invalid role at compile time; no reflection or
  generated component-name API is involved. Generated `Add<Component>()` methods are removed.
- Authorizers form an all-of pipeline. Their `IRequestAuthorizer<TScope>` scope may be a concrete
  request, request-family interface, or `IRequestBase`; matching policies run by semantic stage
  and then registration order before the handler is resolved.
- Remove `AddGeneratedEvents()` calls. Generic component registration contributes events declared
  by the calling assembly and external event types it actually uses; unrelated events from a
  referenced package stay out of the catalog. `AddEvent<T>()` remains a low-level escape hatch for
  events hidden from compile-time analysis.
- Replace per-request and raw-route listener registration with `AddRequestWorkers()`. Use
  `AddRpcWorkers()`, `AddQueueWorkers()`, `AddNoticeWorkers()`, or `AddScheduledWorkers()` when a
  deployment hosts only selected transport kinds. Workers are derived only from selected handlers;
  outbound-only inferred requests never become listeners. Every `AddFitz`/`UseFitzClient` overload
  declares all selected request transports by default, even when its callback only configures a
  fleet. The first transport-specific method narrows that default; use `DisableRequestWorkers()` to
  host component workloads without request listeners. Explicit selectors that match no selected
  handler now fail startup before Fitz connects.
- Run projectors and reactors through `AddProjector<T>(...)`/`AddReactor<T>(...)` plus
  `AddWorkers()`. `AddPortiaProjectorRunner<T>()`, `AddPortiaReactorRunner<T>()`,
  `ProjectorHostedService`, and `ReactorHostedService` are removed; a host with no
  `IWorkloadCoordinator` now runs its workloads in-process, so no coordination
  infrastructure is needed to replace them.
- Replace scope-selection lambdas with the explicit scope argument:
  `AddProjector<T>(WorkloadScope.PerTenant)` or `AddReactor<T>(WorkloadScope.Global)`.
  Pass an optional second lambda only for names, polling, batching, or rebuild settings.
- `IRequestBus` declares three dispatch primitives plus `CreateContext`. The
  `SendAsync`/`StreamAsync` overloads are extension methods in `RequestBusExtensions`, so
  call sites are unchanged but a custom bus implements four members instead of nine.
- Construct an aggregate normally and call `HydrateAsync(aggregate, ct)`.
- Save raised events or audits with `SaveAsync(aggregate, context, ct)`.
- Implement the full execution context contract in custom buses and transport adapters.
- Drain or discard every version-1 queued, noticed, or scheduled request before deployment, then send only version-2 envelopes containing an explicit contract discriminator and validated logical request metadata.
- Execute reactions as system principals, with their actual triggering event.
- Use `BaseProjector`, `BaseBatchProjector`, `BaseReactor`, or `BaseBatchReactor`,
  passing the persistence contract through the constructor. Context carries metadata only.
- Keep projector data and checkpoints in one atomic repository commit. Reactor checkpoints
  use the full `CheckpointIdentity`: component, canonical pattern, and rebuild identity
  where applicable. There are no name-only checkpoint migration adapters.

Existing stored domain events still have an explicit schema-evolution contract:
upcasters transform business payloads while preserving event metadata. This is part
of operating an event store; it does not add compatibility overloads to application APIs.

Use [getting started](getting-started.md), [application setup](application-setup.md),
and [request and reaction context](request-context.md) as the supported application
workflow. Historical defect evidence records earlier implementation stages and is
not an API guide.

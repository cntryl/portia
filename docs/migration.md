# Current API contract

Portia is being designed as a greenfield framework. The current API has no legacy
aggregate factories, ID-based repository loading, unattributed repository saves,
or context-dropping adapter fallbacks.

- Configure the application once with `AddPortia`; activate its declared workers
  only in the worker deployment with `AddWorker`.
- Select handlers and authorizers independently with `AddRequestHandler<T>()` and
  `AddRequestAuthorizer<T>(AuthorizationStage)`. Register a
  sender-only contracts are inferred from strongly typed dispatch calls. Portia.Generators replaces the stable generic
  calls with typed descriptors and reports an invalid role at compile time; no reflection or
  generated component-name API is involved. Existing generated `Add<Component>()` methods remain
  compatibility shims for this release.
- Authorizers form an all-of pipeline. Their `IRequestAuthorizer<TScope>` scope may be a concrete
  request, request-family interface, or `IRequestBase`; matching policies run by semantic stage
  and then registration order before the handler is resolved.
- Remove `AddGeneratedEvents()` calls. Generic component registration contributes the accessible
  domain events known to the calling compilation. `AddEvent<T>()` remains a low-level escape hatch
  for events unavailable at compilation.
- Prefer `AddQueueWorker<TRequest>()`, `AddNoticeWorker<TRequest>()`, and
  `AddScheduledWorker<TRequest>()` to repeated raw transport routes. The request must already be
  selected through `AddRequestHandler<THandler>()`, inferred dispatch, or the `RegisterDynamicRequest<TRequest>()` escape hatch; raw routes remain supported.
- Run projectors and reactors through `AddProjector<T>(...)`/`AddReactor<T>(...)` plus
  `AddWorker()`. `AddPortiaProjectorRunner<T>()`, `AddPortiaReactorRunner<T>()`,
  `ProjectorHostedService`, and `ReactorHostedService` are removed; a host with no
  `IWorkloadCoordinator` now runs its workloads in-process, so no coordination
  infrastructure is needed to replace them.
- `WorkloadOptions.Global()`/`PerTenant()` return `void`; call them as statements.
- `IRequestBus` declares three dispatch primitives plus `CreateContext`. The
  `SendAsync`/`StreamAsync` overloads are extension methods in `RequestBusExtensions`, so
  call sites are unchanged but a custom bus implements four members instead of nine.
- Construct an aggregate normally and call `HydrateAsync(aggregate, ct)`.
- Save raised events or audits with `SaveAsync(aggregate, context, ct)`.
- Implement the full execution context contract in custom buses and transport adapters.
- Send version 1 request envelopes containing validated logical request metadata.
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

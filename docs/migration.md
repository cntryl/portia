# Current API contract

Portia is being designed as a greenfield framework. The current API has no legacy
aggregate factories, ID-based repository loading, unattributed repository saves,
or context-dropping adapter fallbacks.

- Configure the application once with `AddPortia`; activate its declared workers
  only in the worker deployment with `AddWorker`.
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

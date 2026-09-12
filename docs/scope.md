# Scope

This page states what Portia does and does not do. Nothing here is pending; each line is a
current, deliberate boundary.

- Portia's own runtime code avoids reflection: JSON uses source-generated contexts and
  component registration uses compile-time generated descriptors. Runtime packages set
  `IsAotCompatible=true` and run the resulting AOT/trim analyzers. CI publishes and executes an
  external, packed ASP.NET Core consumer as a native `linux-x64` executable with
  `PublishAot=true`. This proves the supported Portia path, while an application's own
  serializers, converters, generated contexts, and other dependencies remain that application's
  responsibility.
- The pinned Cntryl.Fitz packages advertise `IsAotCompatible`; NativeAOT compatibility for an
  application that uses `Portia.Fitz` still depends on its own serializers, converters, and other
  dependencies.
- Aggregate snapshotting is not supported and is not planned. Aggregates are expected to stay
  bounded enough to rehydrate directly from their event streams. Prefix truncation is also
  unsupported: readers must continue to expose original contiguous physical offsets.
- Atomic event-store writes cover one aggregate stream. Portia has no multi-stream transaction or
  transactional request outbox; cross-aggregate work uses at-least-once events and reactors.
- Reactor effects are at-least-once by design. Applications must make their reaction effects
  idempotent.
- Each queue runner reserves, dispatches, and acknowledges one delivery at a time. Portia scales
  queue throughput through additional processes and routes; it does not provide in-process
  prefetch or parallel-delivery controls.
- HTTP request bodies are fully buffered object JSON, bounded to 10 MiB by default. Multipart,
  form, binary, and streaming-body inputs are not supported.
- Portia never skips a poison projection or reaction event. Hosted passes retry with bounded
  exponential backoff and then fault the worker, leaving the last successful checkpoint intact.
- Durable event persistence is bundled for Fitz only. `Portia.Fitz` supplies `FitzEventStore`,
  `FitzKvProjectionStore` — an abstract base the application's own repository derives from, so its
  projection writes share the transaction Portia commits the checkpoint in — and
  `FitzKvCheckpointStore` for reactor progress, selected with `UseKvCheckpoints`. See
  [projectors and reactors](projectors-and-reactors.md) for the wiring.
- Read-model storage is an open userland boundary, not restricted to Fitz or a first-party engine.
  An application repository can use PostgreSQL, Snowflake, SQL Server, Redis, or another backend's
  native client and implement `IProjectionStore`/`IProjectionBatch`; it needs no custom Portia
  runner or generic storage DSL. If the backend cannot atomically commit model changes and the
  checkpoint, integrate it as an at-least-once reactor effect instead of weakening the projector
  contract.
- Portia does not currently ship PostgreSQL, Snowflake, SQL Server, Redis, or other general-purpose
  durable persistence adapters.
  `Portia.Testing` supplies backend-neutral projection and optional reaction deduplication
  conformance suites so application implementations can prove the required invariants for any
  other backend.

See [design decisions](design-decisions.md) for the reasoning and enforcement behind these
boundaries. See [performance and scaling](performance-and-scaling.md) for measured hot paths,
operational limits, and the runtime scaling model. The accepted Fitz-Portia-Cassie layering and
open read-model boundary are fixed in [platform vision](platform-vision.md).

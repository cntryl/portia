# Scope

This page states what Portia does and does not do. Nothing here is pending; each line is a
current, deliberate boundary.

- Portia's own runtime code avoids reflection: JSON uses source-generated contexts and
  component registration uses compile-time generated descriptors. Runtime packages set
  `IsAotCompatible=true` and run the resulting AOT/trim analyzers. Portia does not build or
  publish a native `PublishAot` executable, so it makes no claim about an executed native binary.
  AOT/trim compatibility of an application's own
  serializers, converters, generated contexts, and other dependencies is that application's
  responsibility, not Portia's.
- The pinned Cntryl.Fitz package does not itself advertise `IsAotCompatible`; NativeAOT
  compatibility for applications that use `Portia.Fitz` also depends on that upstream package.
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
- Portia does not currently ship Postgres, SQL Server, Redis, or other durable persistence
  adapters. `Portia.Testing` supplies backend-neutral projection and optional reaction
  deduplication conformance suites so application implementations can prove the required
  invariants until official adapters arrive.

See [design decisions](design-decisions.md) for the reasoning and enforcement behind these
boundaries. See [performance and scaling](performance-and-scaling.md) for measured hot paths,
operational limits, and the runtime scaling model.

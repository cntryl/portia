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
  bounded enough to rehydrate directly from their event streams.
- Reactor effects are at-least-once by design. Applications must make their reaction effects
  idempotent.
- Portia does not currently ship Postgres, SQL Server, Redis, or other durable persistence
  adapters. `Portia.Testing` supplies backend-neutral projection, fencing, and optional reaction
  deduplication conformance suites so application implementations can prove the required
  invariants until official adapters arrive.

See [design decisions](design-decisions.md) for the reasoning and enforcement behind these
boundaries.

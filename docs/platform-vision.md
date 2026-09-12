# Platform vision

**Status: accepted.** This page fixes the product boundary for Fitz, Portia, and Cassie. It is not
a claim that every integration described here is already delivered; [scope](scope.md) remains the
authority for current support, and the changelog records completed behavior.

## One platform, three distinct layers

| Layer | Owns | Does not own |
|---|---|---|
| Fitz | The authoritative event log, RPC, queues, notices, schedules, leases, and distributed coordination | Application command, aggregate, projection, or query semantics |
| Portia | Commands, handlers, authorization, aggregates, event production, projection and reaction execution, checkpoints, tenancy, and worker lifecycle | A database-independent query language or storage engine |
| Cassie | The first-party read-model engine, with native SQL, graph, time-series, and vector capabilities | The authoritative domain event history or Portia's execution semantics |

Committed Fitz events are the source of truth. Portia consumes them to build derived read models.
Cassie stores and serves those models, and application handlers query it through ordinary
application repositories. Command handlers do not dual-write event state into Cassie.

These boundaries are permanent even when the packages are deployed together. Portia must not
expose Cassie-specific query primitives from its core contracts, and Cassie must not become a
second authority for aggregate history.

## Cassie is first-party, not mandatory

Cassie is the batteries-included read-model choice for the Cntryl stack. It can provide a deeper
integration than a generic adapter because one engine owns relational, graph, time-series, and
vector representations. That privileged implementation status does not make it mandatory.

An application may build a projection in PostgreSQL, Snowflake, or another backend without a
Portia fork, a custom runner, or a framework-owned storage DSL. The projector constructor receives
an application repository using that backend's native client and data model. The repository
implements `IProjectionStore`; its unit-of-work handle implements `IProjectionBatch`.

Portia deliberately specifies lifecycle and correctness rather than tables, documents, vertices,
time-series samples, embeddings, indexes, or query syntax. A backend integration retains its native
schema, query language, optimizer, indexing, and operational tools. Different projectors in the
same application may target different stores.

## The projection correctness boundary

A transactional projection store must:

1. Load the authoritative checkpoint for a `CheckpointIdentity`.
2. Begin one isolated unit of work for the projection batch.
3. Include every application read-model mutation made through that repository in the unit.
4. Commit those mutations and the next checkpoint atomically.
5. Discard uncommitted work on disposal and reject a stale checkpoint with
   `ProjectionConcurrencyException`.
6. Isolate rebuild generations until the application deliberately promotes one.

The backend's brand does not determine whether it qualifies. PostgreSQL commonly supplies the
required transaction directly. Snowflake or any other analytical store qualifies as an
`IProjectionStore` only when the selected ingestion path can bind visible model changes and the
Portia checkpoint to the required commit boundary.

When a target cannot provide that boundary, it is an external effect rather than a transactional
projection. Integrate it through a reactor with at-least-once delivery and a deterministic
idempotency strategy. Portia must not weaken `IProjectionStore` or report a checkpoint as committed
before its associated model changes are durably committed merely to make an adapter fit.

## Definition of an easy userland addition

A PostgreSQL, Snowflake, or other userland read-model integration is considered easy when it
requires only:

- an application repository and batch implementation over the backend's public client;
- ordinary dependency-injection registration;
- the application's projector logic and native schema or query definitions; and
- an `IProjectionStoreConformanceProbe` proving Portia's atomicity, rollback, concurrency, and
  rebuild-generation contract.

It must not require changes to Portia, internal APIs, a new processor, transport-specific dispatch,
assembly scanning, or translation into a lowest-common-denominator read-model abstraction.
Portia may ship optional integration conveniences, but the core runtime must not branch on the
chosen read-model backend.

## Cassie delivery gates

The first-party Cassie integration is complete only when it:

- satisfies `ProjectionStoreConformance` through the public contracts used by userland adapters;
- proves crash recovery and atomic checkpoint behavior for supported write combinations;
- isolates, resumes, and promotes rebuild generations without exposing partial replacement state;
- preserves stable tenant, projection, and model identities without deriving them from CLR names;
- demonstrates deterministic replay and bounded batch/resource behavior;
- defines visibility and freshness semantics for SQL, graph, time-series, and vector indexes; and
- emits backend telemetry that correlates with Portia while preserving Portia's cardinality and
  privacy constraints.

These gates make Cassie the reference implementation of the open projection boundary, not a
special case that bypasses it.

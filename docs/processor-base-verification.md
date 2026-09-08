# Processor base verification

> Dated snapshot (2026-09-06). This records milestone evidence. See
> [projectors and reactors](projectors-and-reactors.md) for the current API and
> [known limitations](known-limitations.md) for unresolved gaps.

Verified locally on 2026-09-06. See [projectors and reactors](projectors-and-reactors.md)
for the application API and backend implementation requirements.

## Red → green

The initial public consumer test failed to compile because `BaseProjector`,
`BaseBatchProjector`, `IProjectionStore`, and the non-generic batch/context contracts
were absent. The implementation now has all four requested base classes and requires
the persistence dependency through the constructor.

Behavioral tests cover:

- Single-event versus bounded-batch commit boundaries for both projectors and reactors.
- A repository implementing both application operations and projection persistence,
  without a separately registered target or service access through context.
- Buffered projection writes discarded on failed commit or cancellation, with progress
  unchanged and disposal observed.
- Replay after reaction checkpoint failure, preserving each triggering event's cause
  while assigning fresh system execution identities.
- Generated batch handlers preserving source order across event types and dispatching
  more-derived event handlers first, with rebuild metadata intact.
- Batch-only handler interfaces rejected on single-event bases (`PORTIA017`).
- Existing uncertain-commit/checkpoint-reload regressions, mixed global/per-tenant
  progress, scoped disposal, and real Fitz ownership handoff.

The old compatibility-only constructor/overload reflection tests were removed with
those obsolete signatures. The framework remains greenfield.

## Final validation

- Locked restore passed.
- Release build passed with zero warnings and errors.
- Formatting verification and `git diff --check` passed.
- **406 tests passed:** 201 framework tests and 205 consumer tests, with no skips.
- Broker tests used the pinned Compose image in an isolated project on port 14090.

Local packages `1.0.0-bases-20260906` were consumed outside the repository by separate
API and worker executables sharing one application project. They had no Portia source
project references or repository build settings. The projector receives its concrete
repository through its constructor. Both the projector and reactor use generated batch
handlers from the installed generator package.

Direct HTTP and HTTP 202 queue publishing completed command → event → batch reaction →
event → batch projection. The API accepted requests with the worker stopped; starting
the worker produced amounts 8 and 13 with intact actor attribution, per-event causation,
UUIDv4 event/execution identities, and a UUIDv5 receipt aggregate identity.

The package proof uses a file projection repository and in-memory reactor checkpoints.
EF Core, ADO.NET, and DynamoDB requirements are documented from their official transaction
contracts; no database-specific adapters or integration qualification for those backends
are claimed. No packages were published and no deployment was performed.

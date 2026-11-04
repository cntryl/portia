# Domain, persistence, operations, and tenant-cursor pass

Product source remained read-only. Proofs were added only beside this note. The root reviewer owns
compilation, execution logs, source freezing, and the consolidated finding count.

## Source-backed candidates and proofs

1. **Tenant lifecycle extraction failure consumes the record.**
   `EventSourcedTenantDirectory.Cursor.ReadAsync` sets `_nextCursor` at source line 332 before
   `Apply` invokes the application's tenant extractor at line 335. Re-enumerating the same cursor
   after an extractor exception starts after the failed lifecycle event. The start and stop cases in
   `Pass2TenantCursorFailureTests` show the next tenant instead of the omitted acme transition.
   The root reviewer confirmed both RED cases.
2. **Subscription cleanup failure permanently reserves the cursor.**
   The same iterator's `finally` awaits subscription disposal at line 373 before resetting `_reading`
   at line 376. An `IOException` from disposal prevents that reset. Subsequent reads fail the
   concurrent-enumeration guard, and `MultiTenantRunner` reconnects to that same unusable cursor.
   The fixture includes failing-cleanup and ordinary-cleanup controls; root confirmed RED/control.
3. **Lost attempt-start acknowledgement duplicates its durable identity.**
   `OperationalSession.BeginAsync` chooses one attempt UUID, then unconditionally appends it within
   the retried `ChangeAsync` closure at lines 194-197. A persisted CAS whose acknowledgement is lost
   is reloaded and appended a second time. Completion and failure recording use `Single` with that
   UUID, so a normal reactor cannot complete its receipt/checkpoint after applying effects.
   `FitzOperationalStore.WriteAsync` can surface an unknown native commit outcome at lines 174-181.
   `Pass2OperationalStartAcknowledgementTests` models exactly that post-commit acknowledgement
   failure, alongside a no-loss control. Root confirmed two failures and one passing control.
4. **Rare manual-reactor effect identity alias.**
   `Reactor.CreateEffectId` concatenates unescaped fields at lines 102-103. For source UUID `e`,
   the admitted manual tuple `(reactor="billing", effect="reserve:e:audit")` produces exactly the
   same UUID input as `(reactor="billing:e:reserve", effect="audit")`. These are different logical
   effects whose idempotency keys should differ. `Pass2ManualEffectIdentityTests` uses the public
   reaction context and the supported protected method, with an ordinary distinct-name control.
   This needs unusually composed names containing source UUID text; it is not an unconditional
   failure of hosted reactors or ordinary fixed names. Existing persisted-ID compatibility tests
   constrain remediation. Root confirmed the collision failure (both keys
   `6c80a189-65e0-5ca1-8473-f0d8e291fa1c`) and the passing ordinary-name control.

## Additional source coverage

- Aggregate hydration, physical stream offsets, duplicate event identities, whole-batch validation,
  apply failure invalidation, save attribution freezing, audit separation, commit/discard semantics,
  cancellation, and optimistic concurrency.
- Domain-event metadata, envelope serialization, one-time metadata attachment, record copying,
  exact schema resolution, upcaster graph validation, and malformed-envelope fail-closed paths.
- Projector and reactor batch/pass budgets, explicit failure Results, cancellation boundaries,
  native checkpoint authority, transactional projection commit/disposal, source cleanup preservation,
  principal snapshots, manual rebuild rejection, and stable effect-key composition.
- Fitz stream append/commit/rollback, post-commit cleanup handling, resource and pattern offset
  validation, bounded progress probes, workload resource isolation, checkpoint encoding, conditional
  checkpoint saves, projection batch reservation, and tenant snapshot compare/exchange reconciliation.
- Cassie metadata schema classification, stable length-encoded projection keys, native transaction
  ownership, revision CAS, cleanup paths, active query/batch gating, and read-session lease release.
- Operational required-journal retries, owner fencing, atomic reactor receipts, projector confirmation,
  retention, queued-plan invalidation, exact approvals, failure backoff, and interrupted replay.
- Request registry/pipeline shape dispatch, authorization/guard ordering, initialized Results,
  continuation single-use state, and the extended behavior-count path.
- Fixed/event-sourced tenant directory membership, snapshot restoration, resumable initial/live
  transitions, notifier polling, interruption, and cursor cleanup.

## Candidates excluded or left unresolved

- **Claimed repair after process restart:** ownership acquisition marks running repairs indeterminate
  and permits automatic crash replay. That differs from the recently guarded in-process
  projection-acknowledgement path, but existing durability tests and operations documentation
  explicitly permit automatic crash replay. No clearer supported promise was found; do not turn
  that policy asymmetry into a confirmed defect.
- **Concurrent reuse of one mutable aggregate across command callbacks:** per-method operation guards
  do not make arbitrary application callbacks thread-safe. No explicit supported shared-instance
  execution contract was found, so this was not elevated.
- **Malformed user-created cursor text:** ordinary opaque tokens returned by Fitz remain valid;
  no real provider-issued malformed UTF-16 token was established.
- **A provider returning a different route or non-monotonic cursor:** pattern reads check this, while
  exact-resource reads rely on the provider's exact-route contract. A deliberately nonconforming
  provider alone does not establish a framework defect.
- **Concurrent disposal of a borrowed Cassie data source or repository during an unfinished call:**
  documented lifetime requirements do not promise arbitrary use after scope disposal.
- **Mutating domain-event payloads after append, or a custom JSON converter changing its request:**
  no immutable snapshot guarantee for arbitrary mutable application-owned objects was found.

## Bounded Jev question suggestions

Use the corresponding method bodies as shared state; acceptance remains source and executable proof.

- Does a tenant extractor exception occur after the resumable cursor has saved the record's next offset?
- Can subscription disposal throw before the cursor releases its enumeration reservation?
- Can retrying a persisted-but-unacknowledged attempt start append its already-used attempt UUID again?
- Does the subsequent reactor completion assume exactly one durable attempt has that UUID?

The effect-ID alias is a deterministic string equality and is established directly by the test;
there is no need to ask a model to calculate whether the two hash inputs are equal.

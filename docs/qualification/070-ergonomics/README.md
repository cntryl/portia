# Application/testing ergonomics qualification (#118–#120, #122)

## Per-issue controls

- #118: corrected public XML distinguishes transaction batch size, source-record pass budget and
  checkpoint catch-up. ManualBoundedPassExample in the independently packed projection consumer
  reads 4,096 + 1 records through both runners, verifies empty/no-progress passes and cancellation.
  Existing checkpoints suffice: no public result-type addition is needed.
- #119: all original When signatures remain; three token overloads pass the caller token to the
  real bus. RequestScenarioTests verifies command/query cancellation through authorization,
  guards, behavior and handler, plus asynchronous scope disposal. Stream cancellation disposes
  enumeration and scope. Exceptions remain cancellation rather than successful outcomes.
- #120: positive limits validated synchronously before dispatch; exact observed sequence and
  enumerated-item count prove no extra read. Early completion is accepted and repeated await
  reuses the operation. All additive overloads execute in the reflection-disabled packed consumer.
- #122: actual WellKnownFixAllProviders.BatchFixer invoked at document/project/solution scope,
  multiple documents/projects, both processors, shared nested enclosing types, comments/modifiers,
  idempotence and generated compilation. Unsupported shapes remain excluded. Portable external-file
  diagnostic locations are associated with IDE workspace trees by the fixture. Cache discovery
  requires both core and HTTP generator assemblies, with recursive field/generic-container checks.

## Verification and adversarial review

Full locked restore and Release build passed with zero warnings/errors. Full solution suite:
1,141 core + 507 compiler/consumer + 71 existing MCP + 3 reflection-disabled, zero failures/skips.
Two additional adversarial generic-container controls were subsequently added; all 17 focused
FixAll/cache tests and all 29 strengthened request-scenario tests pass. Full solution formatting
verification passed; the final request fixture formatting was also applied.

Fresh qualification packages (not release publication) use version 0.7.0-qualification.ergonomics.
The projection-store consumer restored into /private/tmp/portia-ergonomics-consumer-cache and ran
with JSON reflection disabled, including every new request overload and the manual example.
Existing CI independently covers packed analyzer assets, HTTP generator ownership and Linux NativeAOT.

Review: no signature removals, bound validation precedes registry/dispatch, the limit is tested
against actual produced items, await-foreach break unwinds async enumeration before the existing
async service scope exits, and cancellation exceptions are not swallowed by the settled-denial
logic. Tooling changes are test-only and do not claim proof of arbitrary application purity.
MCP helper changes remain isolated in the preserved earlier worktree.

Raw command output is retained with hashes. Empty consumer logs indicate silent successful
assertions; the shell pipeline completed with exit status zero. Hosted final-head and exact merged
commit CI/CodeQL are separate gates, not inferred from these local controls.

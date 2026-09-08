# Known limitations

This page is the authoritative current limitations list.

- No native `PublishAot` artifact is built or executed by the current qualification. The proof
  level is AOT-analyzer-clean runtime projects plus reflection-disabled CoreCLR tests.
- The pinned Cntryl.Fitz package does not itself advertise `IsAotCompatible`. Portia.Fitz can be
  analyzer-clean, but dependency-level AOT qualification remains upstream work.
- Application serializers, converters, generated contexts, and dependencies are outside Portia's
  AOT guarantee.
- Request-envelope version 2 has no version-1 compatibility reader. Drain or discard all queued,
  noticed, and scheduled version-1 messages before upgrading.
- Aggregate snapshotting is not implemented.
- Reactor effects are at-least-once; applications must make effects idempotent.
- A broker or platform test that was unavailable is unverified, not passing.

# Changelog

Notable changes to Portia. Entries call out anything that changes observable behavior for an
application already running on a previous version, including telemetry, since dashboards and
alerts are as breaking to change as an API.

## Unreleased

### Breaking

- **Consumer-side transport names dropped their `fitz.` prefix.** `RequestInvocation` now supplies
  its own `TransportName`, and inbound telemetry reports the transport *shape* rather than the
  adapter carrying it. The `transport` metric tag and the `messaging.system` activity tag on
  `portia.request.process` and `portia.request.execute` changed:

  | Before | After |
  |---|---|
  | `fitz.rpc` | `rpc` |
  | `fitz.queue` | `queue` |
  | `fitz.notice` | `notice` |
  | `fitz.schedule` | `schedule` |

  `http` and `local` are unchanged. Update dashboards, alert rules, and trace filters that match
  the old values before upgrading. Outbound `portia.request.send` activities raised inside
  `Portia.Fitz` keep their `fitz.*` names: a producer names the adapter it is using, a consumer
  names the shape of the delivery it received.

### Fixed

- A fired durable schedule delivered with a trusted system actor reported `fitz.schedule` while
  every other path reported the invocation's own name, so the transport label depended on which
  authentication path the delivery took. Both paths now read it from the invocation.
- `PortiaWorkloadService` stopped passing an `ILogger<MultiTenantRunner>` when it moved to
  explicit constructor injection, silencing tenant-directory and tenant-start faults for any host
  without an `ActivitySource` listener. The logger is injected and passed through again.
- A pipeline behavior selected by scope but unable to serve the dispatched request's shape — a
  no-result behavior reached through a result-bearing dispatch of a request implementing both —
  threw `InvalidCastException` from inside the bus. Such a behavior is now skipped.
- `PORTIA015` did not cover pipeline behaviors, so a behavior declared in an unsupported shape got
  no diagnostic. Every generator now reads one shared list of component roles.

### Added

- `EventStoreConformance` in `Portia.Testing`: append ordering, contiguous resource offsets,
  offset resumption, pattern-read coverage, and the requirement that a stale append throw
  `EventStreamConcurrencyException` and write nothing.
- Practice diagnostics, all warnings: `PORTIA100` (a projector must not take a dependency that can
  cause an effect), `PORTIA101` (a component must not resolve services from the container),
  `PORTIA102` (an aggregate must not depend on a service), `PORTIA103` (one type, one request
  handler), `PORTIA104` (an unexpected failure stays an exception rather than becoming a failed
  `Result`).

### Changed

- `RequestDeliveryScopes.Fixed` and `FixedQueue` return a new scope per call, matching the port's
  "one independently disposable scope per delivery" contract instead of relying on disposal being
  a no-op.
- Applicable authorizers and behaviors are computed once per request type and cached, rather than
  re-filtered and re-allocated on every dispatch.
- Workload hosting asks a descriptor whether it supports rebuild generations instead of testing
  its type, and Fitz worker definitions build their own runners instead of being switched on.

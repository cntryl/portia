# Explicit workload registration verification

Verified locally on 2026-09-06 at the registration milestone, before the
[processor base refactor](processor-base-verification.md). This supersedes the module-based setup described in
older milestone evidence. See [application setup](application-setup.md) for the current API.

## Red → green

- The initial consumer tests failed to compile because `PortiaBuilder` had no
  `AddProjector`, `AddReactor`, `WorkloadRegistration`, or `WorkloadScope` API.
  Explicit Portia registrations now require `Global()` or `PerTenant()` and reject
  missing/conflicting scope without partially registering a component.
- A lifecycle regression expected `InvalidOperationException` when a coordinator
  stopped unexpectedly, but observed `TaskCanceledException`. Worker shutdown now
  observes both coordination and tenant management and reports unexpected completion
  as a fault. Normal shutdown still cancels and awaits owned work.
- Migrating streaming fixtures exposed an omitted authorizer registration. Explicit
  authorizer registration restored authorization and scope-disposal coverage.

## Behavioral coverage

- Workloads register without Fitz and without starting background workers.
- Infrastructure and workload setup compose in either order, including declarations
  after `AddWorker()` and before the host is built.
- Mixed global/per-tenant projectors and reactors keep checkpoint progress separate.
  Tenant removal stops its workloads; re-addition resumes committed progress without
  replaying already committed effects. Unaffected global ownership remains intact.
- Scoped dependencies see the correct workload identity, tenant, and fencing token;
  scopes are disposed after execution.
- Dynamic fleet partitions preserve retained assignments while removing and adding
  tenant partitions. Real Fitz worker replicas hand off ownership without concurrent
  execution in the broker test.
- Missing tenant directories fail startup before attempting a broker connection.
- Generated request descriptors include only selected handlers and authorizers.
  Unselected conflicting implementations can coexist in an assembly; selecting both
  fails during registration and preserves the prior service collection.
- Module APIs and Fitz-owned component registration methods are removed.

## Final checks

Locked restore, Release build (zero warnings/errors), formatting verification, and
`git diff --check` passed. All **396 tests** passed: **203 framework tests** and
**193 consumer tests**, with no skips. Broker tests used the pinned Compose image
in an isolated project on port 14090.

Local packages `1.0.0-explicit-20260906.2` were consumed outside the repository by
separate API and worker executables sharing one application project. They had no
Portia source project references or repository build settings. The application uses
explicit registrations and `AddPortiaFitz(configuration.GetSection("Fitz"))`.

Direct HTTP and HTTP 202 queue publishing both completed this path:

```text
HTTP → command → event → system reaction → event → projection
HTTP 202 → Fitz queue → command → event → system reaction → event → projection
```

The API accepted requests before the worker started. Starting the worker produced
two projected receipts (amounts 8 and 13), preserving actor and causal attribution,
UUIDv4 execution/event identities, and a deterministic UUIDv5 receipt aggregate ID.

The package proof used one worker, a file projection target, and in-memory reactor
checkpoints. It establishes installed API composition and execution behavior; durable
storage fencing and production autoscaling remain application/deployment responsibilities.
No packages were published and no deployment was performed.

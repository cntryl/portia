# Portia repository instructions

## Build, test, and lint

Portia targets .NET 11 and uses the `Microsoft.Testing.Platform` runner. `global.json` requires
11.0.100 and rolls forward within the .NET 11 feature band. Run commands from the repository root.

```sh
dotnet restore Portia.slnx
dotnet format Portia.slnx --verify-no-changes --no-restore
dotnet build Portia.slnx --configuration Release --no-restore
```

Run the normal service-free suite before integration suites:

```sh
dotnet test --solution Portia.slnx --configuration Release --no-build \
  --filter-not-trait "Category=BrokerIntegration" \
  --filter-not-trait "Category=StorageIntegration" \
  --filter-not-trait "Category=CassieIntegration" \
  --filter-not-trait "Category=DurabilityIntegration"
```

Run one test by fully qualified method name:

```sh
dotnet test --project test/Portia.Tests/Portia.Tests.csproj \
  --configuration Release --no-build \
  --filter-method "Cntryl.Portia.OperationalValidationAuthorityTests.ShouldRecordAuthoritativeRejectionAsSuccessfulProcessingGivenAdvisoryRace"
```

Use `--filter-class`, `--filter-namespace`, or `--filter-trait` for broader selections. Do not mix
simple Microsoft Testing Platform filters with query/VSTest filters.

Integration tests are explicitly classified. Broker tests use the shared Fitz service, storage tests
use Sqrzl, Cassie tests use Fitz plus Cassie, and crash-durability tests start an isolated container:

```sh
docker compose up --detach --wait fitz
dotnet test --project test/Portia.Tests/Portia.Tests.csproj \
  --configuration Release --no-build --filter-trait "Category=BrokerIntegration"
docker compose down --volumes
```

Prefer the setup and environment variables in `CONTRIBUTING.md` when running storage, Cassie, or
durability suites. The broker keeps state, so use `docker compose down --volumes` between suites.
CI also verifies stable dependencies, compiler benchmark fixtures, and the packed-consumer/NativeAOT
path; reproduce those checks from `.github/workflows/ci.yml` when changing generators, packages, or
runtime compatibility.

## Architecture

- `Portia.Abstractions` owns public contracts and stable wire/domain concepts.
  `Portia.Core` implements aggregate execution, request dispatch, checkpointed projectors/reactors,
  and workload coordination. `Portia.DependencyInjection` composes the shared application runtime
  into one generated registry and scopes repository/bus services per request.
- Entry adapters are independent of handlers. ASP.NET Core, MCP, Fitz RPC/queue, schedules, and
  local dispatch all converge on `IRequestBus`. The request pipeline is authorization, behaviors,
  guards, then the handler; transport-specific authentication belongs at the adapter boundary.
- `Portia.Generators`, `Portia.AspNetCore.Generators`, `Portia.Analyzers`, and `Portia.CodeFixes`
  produce registration/transport wiring and enforce contracts. Keep the supported runtime free of
  assembly scanning, reflection fallback, and dynamically named generated types. Contracts and
  events must be represented in application-owned `[PortiaJsonContext]` metadata.
- Aggregates are authoritative only inside one atomically persisted event stream. Portia hydrates
  and commits that stream with optimistic concurrency. There is no multi-stream transaction,
  snapshot support, or transactional outbox; cross-aggregate workflows use durable events and
  idempotent reactors.
- Projectors transform committed events into read models. Their repository model writes and
  checkpoint must be one atomic `IProjectionBatch` commit, and projector work must otherwise remain
  pure. Reactors perform non-transactional effects and may replay, so effects and business command
  identities must be idempotent.
- `Portia.Fitz` supplies the first-party durable event/transport adapter. Read-model storage remains
  backend-native: `Portia.Cassie` and application repositories implement the same projection batch
  contract rather than a universal query DSL. `test/Portia.ConsumerTests` proves the same handler
  through direct, HTTP, RPC, queue, projector, and reactor paths.

## Repository-specific conventions

- Warnings and selected IDE diagnostics are build errors. Use `dotnet format Portia.slnx` as the
  formatting authority, including after changing wrapped C# constructs. Runtime package changes must
  preserve `IsAotCompatible` and generated-metadata requirements.
- Public APIs are tracked per packable `src/` project in `PublicAPI.Shipped.txt` and
  `PublicAPI.Unshipped.txt`; add new unreleased API symbols to `Unshipped.txt`. Keep package versions
  centralized in `Directory.Packages.props`, except standalone examples and package-consumer smoke
  projects, which intentionally declare qualification versions.
- Portia is pre-1.0. Remove obsolete APIs and compatibility shims rather than retaining old
  Portia-format readers or aliases. Reject unsupported formats explicitly, while preserving current
  application event-schema evolution and provider protocols.
- Apply `docs/validation-authority.md` whenever an operation depends on another aggregate or
  external participant. A cross-aggregate read is advisory only, even when fresh. Choreography must
  obtain and consume the participant's committed acceptance or rejection, with explicit
  reject/compensate/intervene resolution. Preserve pending and unknown states until resolved, and
  test the race where an advisory check passes but the participant later refuses.
- Return and record expected business rejection as a domain outcome. A failed projector/reactor
  `Result` means processing is incomplete and prevents checkpoint advancement; retry or worker
  failure does not replace the required business outcome.
- New application-structure behavior belongs in the Roslyn analyzers/code fixes, not in tests that
  inspect repository layout. Follow the closed Common/Core role layout and diagnostic rules in
  `docs/project-structure.md`: one top-level type per named file, feature-local registration,
  Common data-only contracts, and Core behavior.
- Framework public namespaces are compatibility contracts and do not necessarily mirror folders.
  This is distinct from application-structure rules, where namespace and feature folders are
  analyzer-enforced.
- Test projects use xUnit v3 and name tests `Should<Outcome>Given<State>[When<Action>]`. Structure
  every test as `// Arrange`, `// Act`, `// Assert`, with Act performing only the behavior under
  test. Work red-to-green: confirm the new test fails for the expected reason first. Capture an
  expected exception in Act with `Record.Exception`/`Record.ExceptionAsync`, then assert its type
  and details in Assert.
- Use the scenario helpers in `Portia.Testing` for aggregate, request, projector, and reactor
  lifecycle tests. `DomainEvent` equality ignores metadata; use `EventAssert.Equal` when event
  payloads include ordered collections or nested records. A projector test should commit through an
  atomic store contract, while a reactor test must cover replay/idempotency.

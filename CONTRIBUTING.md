# Contributing

Portia builds with warnings treated as errors. Install a stable .NET 10 SDK at or above the
`global.json` floor (10.0.403); the SDK resolver rolls forward within .NET 10 to the latest
installed feature band. Run the fast, broker-free suite first, then run the
separately classified integration suite against Fitz:

```shell
dotnet format Portia.slnx --verify-no-changes
dotnet build Portia.slnx --configuration Release
dotnet test Portia.slnx --configuration Release --no-build --filter "Category!=BrokerIntegration&Category!=StorageIntegration&Category!=CassieIntegration&Category!=DurabilityIntegration"
docker compose up -d --wait
dotnet test Portia.slnx --configuration Release --no-build --filter "Category=BrokerIntegration"
docker compose down --volumes
```

The Compose broker listens on `127.0.0.1:4090` with authentication disabled. Set
`FITZ_TEST_ENDPOINT` to use a different one — a broker that requires credentials is not a
substitute, and the broker keeps state between runs, so bring it down with `--volumes` rather than
reusing it across suites.

Cassie integration tests use the pinned `cassie` image in `compose.yaml` and `Cntryl.Cassie` 0.1.0.
Start both databases and run the complete Fitz event to Portia projector to Cassie query path:

```shell
docker compose up -d --wait fitz cassie
curl --fail --silent --show-error --retry 30 --retry-delay 1 --retry-connrefused --max-time 2 http://127.0.0.1:8080/readyz
FITZ_TEST_ENDPOINT=ws://127.0.0.1:4090/ws \
PORTIA_CASSIE_TEST_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=postgres;Username=root;Password=portia-cassie-test;SSL Mode=Disable;Timeout=15' \
dotnet test Portia.slnx --configuration Release --no-build --filter "Category=CassieIntegration"
docker compose down --volumes
```

`CASSIE_TEST_PORT` and `CASSIE_TEST_HTTP_PORT` override local Compose port bindings; update the
connection string and readiness URL to match. The service uses ephemeral memory storage. Tests
create unique tables and event realms and drop only their own tables. They qualify SQL transaction
atomicity, stale-writer rejection, rollback, generation and pattern isolation, repository recreation,
and the hosted retry policy. They do not qualify persistence across a Cassie server restart.

Mark tests that use the shared Compose Fitz broker with `[Trait("Category", "BrokerIntegration")]`.
Broker fakes and configuration-only tests remain in the broker-free suite.

Operational crash qualification is an xUnit integration test with an isolated persistent Fitz volume
and a child test-worker process. It owns container creation, hard termination, broker recreation,
assertions, and cleanup; it does not use the shared tmpfs broker:

```shell
dotnet test test/Portia.Tests/Portia.Tests.csproj -c Release --no-build --filter "Category=DurabilityIntegration"
```

See [processor operations](docs/operations.md) for the recovery and approval contract.

`dotnet format` is the only tool that decides formatting. Rider's formatter disagrees with it on one
construct — the brace of an initializer that wrapped onto its own line, which Rider indents one
level further than Roslyn does — so turn off **Settings → Tools → Actions on Save → Reformat code**
and clear **Reformat code** in the commit dialog. `IDE0055` is an error here, so Rider's version of
that brace fails the build outright — `dotnet format Portia.slnx` is the fix if you hit it.

Every packable project under `src/` participates in public API tracking. When adding one, create
`PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` beside the project file, each initially
containing `#nullable enable`. Add new, unreleased symbols to `PublicAPI.Unshipped.txt`; move them
to `PublicAPI.Shipped.txt` only as part of release preparation.

Application conventions and their opt-in compiler diagnostics are documented in
[application project structure](docs/project-structure.md). Keep enforcement in analyzers and
test their observable diagnostics and code fixes; do not add tests that inspect repository layout.

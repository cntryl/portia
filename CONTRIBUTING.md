# Contributing

Portia builds with warnings treated as errors. Run the fast, broker-free suite first, then run the
separately classified integration suite against Fitz:

```shell
dotnet format Portia.slnx --verify-no-changes
dotnet build Portia.slnx --configuration Release
dotnet test Portia.slnx --configuration Release --no-build --filter "Category!=BrokerIntegration"
docker compose up -d --wait
dotnet test Portia.slnx --configuration Release --no-build --filter "Category=BrokerIntegration"
docker compose down --volumes
```

The Compose broker listens on `127.0.0.1:4090` with authentication disabled. Set
`FITZ_TEST_ENDPOINT` to use a different one — a broker that requires credentials is not a
substitute, and the broker keeps state between runs, so bring it down with `--volumes` rather than
reusing it across suites.

Mark every test that opens a real Fitz connection with `[Trait("Category", "BrokerIntegration")]`.
Broker fakes and configuration-only tests remain in the broker-free suite.

`dotnet format` is the only tool that decides formatting. Rider's formatter disagrees with it on one
construct — the brace of an initializer that wrapped onto its own line, which Rider indents one
level further than Roslyn does — so turn off **Settings → Tools → Actions on Save → Reformat code**
and clear **Reformat code** in the commit dialog. `IDE0055` is an error here, so Rider's version of
that brace fails the build outright — `dotnet format Portia.slnx` is the fix if you hit it.

Every packable project under `src/` participates in public API tracking. When adding one, create
`PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` beside the project file, each initially
containing `#nullable enable`. Add new, unreleased symbols to `PublicAPI.Unshipped.txt`; move them
to `PublicAPI.Shipped.txt` only as part of release preparation.

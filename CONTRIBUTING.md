# Contributing

Portia builds with warnings treated as errors. Keep formatting, analyzers, and tests green with:

```shell
dotnet format Portia.slnx --verify-no-changes
dotnet build Portia.slnx --configuration Release
dotnet test Portia.slnx --configuration Release --no-build
```

Every packable project under `src/` participates in public API tracking. When adding one, create
`PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` beside the project file, each initially
containing `#nullable enable`. Add new, unreleased symbols to `PublicAPI.Unshipped.txt`; move them
to `PublicAPI.Shipped.txt` only as part of release preparation.

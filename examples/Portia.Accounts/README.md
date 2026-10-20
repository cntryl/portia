# Packed Account CQRS and OpenTelemetry example

This example puts one Account feature composition in `Portia.Accounts.Application`. The API,
worker, and application tests all use it. The feature opens accounts and deposits funds through an
aggregate repository, projects committed events with a pure reducer into an ordinary Fitz KV
repository, and serves a read query from that projection. The request pipeline applies Portia's tenant authorization
and the feature's account authorization, a behavior, a configured-tenant guard, an operational guard,
and then the handler.

The API enables its demo header authentication only in Development. Send both `X-Demo-Tenant` and
`X-Demo-Account`; the account grant is scoped to that tenant. Configure a real default authentication
scheme and account authorization for other environments; the secure host profile fails startup when
no default scheme is configured. The `Accounts:Tenant` setting defaults to `demo` only in Development
and must be set explicitly in every other environment. Fitz and the OTLP collector are local demo
services; Fitz queue/KV/event state uses a disposable local filesystem.

## Run from a checkout

Build and test the source projects, then start the local broker and collector:

```sh
NUGET_PACKAGES=/tmp/portia-account-nuget dotnet restore \
  examples/Portia.Accounts/Portia.Accounts.Tests/Portia.Accounts.Tests.csproj \
  --configfile examples/Portia.Accounts/NuGet.Config
NUGET_PACKAGES=/tmp/portia-account-nuget dotnet test \
  examples/Portia.Accounts/Portia.Accounts.Tests/Portia.Accounts.Tests.csproj \
  --configuration Release
docker compose -f examples/Portia.Accounts/compose.yaml up -d --wait
```

Run the worker in one terminal:

```sh
Accounts__Tenant=demo \
Fitz__Endpoint=ws://127.0.0.1:4090/ws \
Fitz__ApplicationName=portia-accounts \
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317 \
OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
dotnet run --project examples/Portia.Accounts/Portia.Accounts.Worker \
  --configuration Release
```

Run the API in another terminal:

```sh
Accounts__Tenant=demo \
Fitz__Endpoint=ws://127.0.0.1:4090/ws \
Fitz__ApplicationName=portia-accounts \
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317 \
OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
ASPNETCORE_URLS=http://127.0.0.1:5088 \
ASPNETCORE_ENVIRONMENT=Development \
dotnet run --project examples/Portia.Accounts/Portia.Accounts.Api \
  --configuration Release
```

Open an account, deposit funds, and read the asynchronously projected balance:

```sh
account_id=01234567-89ab-4cde-8fab-0123456789ab
curl --fail-with-body -i -X POST \
  -H 'Content-Type: application/json' -H 'X-Demo-Tenant: demo' -H "X-Demo-Account: $account_id" \
  -d "{\"account_id\":\"$account_id\",\"initial_balance_cents\":500}" \
  "http://127.0.0.1:5088/tenants/demo/accounts/$account_id/open"
curl --fail-with-body -i -X POST \
  -H 'Content-Type: application/json' -H 'X-Demo-Tenant: demo' -H "X-Demo-Account: $account_id" \
  -d "{\"account_id\":\"$account_id\",\"amount_cents\":125}" \
  "http://127.0.0.1:5088/tenants/demo/accounts/$account_id/deposits"
curl --fail-with-body -H 'X-Demo-Tenant: demo' -H "X-Demo-Account: $account_id" \
  "http://127.0.0.1:5088/tenants/demo/accounts/$account_id"
```

The query should return `balance_cents: 625` after the worker projects both events. Stop both .NET
hosts with Ctrl+C so they flush telemetry, inspect the receiver output, and stop the local services:

```sh
docker compose -f examples/Portia.Accounts/compose.yaml logs --no-color otel-collector
docker compose -f examples/Portia.Accounts/compose.yaml down
```

The shared `AccountsTelemetry.AddAccountsTelemetry` setup configures both executable hosts with
Portia tracing, metrics, structured-log instrumentation, and OTLP exporters. The API adds the
host-specific `OpenTelemetry.Instrumentation.AspNetCore` package and instrumentation through that
same setup helper, so HTTP server spans provide the sampling parent for Portia request spans. The
worker exports its structured startup log and worker metrics under its own service name. The
collector receives all three signal types and prints service resources, trace/span IDs, metrics,
and structured log attributes. Both executable hosts use `await using` so their telemetry providers
are disposed after the graceful host shutdown. Portia telemetry tests in the solution separately
exercise the fast in-memory exporter controls.

## Qualify freshly packed packages

The package gate switches the example's conditional references to `Cntryl.Portia.*` packages and
uses a fresh NuGet cache. It fails if the selected package version is missing from
`artifacts/packages`:

```sh
dotnet restore Portia.slnx --locked-mode
dotnet build Portia.slnx --configuration Release --no-restore
dotnet pack Portia.slnx --configuration Release --no-build --no-restore \
  --output artifacts/packages -p:PackageVersion=0.9.0
PORTIA_SMOKE_PACKAGE_VERSION=0.9.0 \
  scripts/qualify-account-example.sh /tmp/portia-account-otel-evidence
```

The qualification script restores and builds the tests, API, and worker against those packed
packages, runs the feature tests, drives the API and worker against the pinned Fitz broker, and
checks actual OTLP receiver output for traces, metrics, structured logs, service names, trace
correlation, and export after graceful host shutdown.

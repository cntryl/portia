# Portia

Portia is a .NET 11 application framework for event-sourced decisions and
durable work. Register a handler once and expose it through the entry points
your application supports, including HTTP, MCP, RPC, queues, and local dispatch.
Entry points share the authorization and guard pipeline. Portia carries actor
identity and causation across delivery boundaries.

Aggregates record decisions with optimistic concurrency. Projectors update
read models and commit their checkpoints with the updates. Reactors handle
follow-up work and can replay, so applications make those effects idempotent.
For work spanning aggregates, a guard can catch predictable failures early.
Completion depends on the participants' committed outcomes.

## Install

Packages are available from the Cntryl GitHub Packages feed. Follow the
[feed and credential setup](docs/getting-started.md) before installing.

```sh
dotnet add package Cntryl.Portia.AspNetCore
dotnet add package Cntryl.Portia.Analyzers
```

These are the starting packages for an HTTP host with compile-time registration
and diagnostics. Use `Cntryl.Portia.DependencyInjection` to compose a host without
HTTP, `Cntryl.Portia.Fitz` for Fitz persistence and transports, and
`Cntryl.Portia.Cassie` for Cassie projection repositories. Keep the Portia
packages on the same version.

## Quick start

Follow the [getting-started guide](docs/getting-started.md) to define a request,
register its handler, and expose it through a host. The
[Accounts example](examples/Portia.Accounts/README.md) runs the complete flow
locally, from opening an account and depositing funds to reading its projected
balance. It includes a separate worker and OpenTelemetry setup.

## Guides

| Task | Guide |
| --- | --- |
| Compose an API or worker | [Application setup](docs/application-setup.md) · [Project structure](docs/project-structure.md) |
| Define a request and authorize its actor | [Contracts](docs/contract-attributes.md) · [Request pipeline](docs/request-pipeline.md) · [Request identities](docs/request-identities.md) |
| Apply events and coordinate decisions across aggregates | [Projectors and reactors](docs/projectors-and-reactors.md) · [Validation authority](docs/validation-authority.md) |
| Use a backend for projections | [Cassie repositories](docs/cassie-projections.md) · [Projection store contract](docs/projectors-and-reactors.md) |
| Expose tools or generate API descriptions | [MCP](docs/mcp.md) · [OpenAPI](docs/openapi-descriptions.md) |
| Connect application services | [Data protection](docs/data-protection.md) · [Secrets](docs/secrets.md) · [Object storage](docs/object-storage.md) · [Mail and SMS](docs/communications.md) |
| Test, deploy, or troubleshoot | [Testing](docs/testing.md) · [Operations](docs/operations.md) · [Observability](docs/observability.md) · [Native AOT](docs/native-aot.md) |
| Understand framework boundaries | [Scope](docs/scope.md) · [Design decisions](docs/design-decisions.md) |

## Choose a Fitz primitive

| Use | When |
| --- | --- |
| Event streams | Store authoritative aggregate history for replay. Each append is atomic within one stream. |
| KV | Store keyed current state, derived data, or processor checkpoints. |
| Queues | Send work to one competing consumer. Accepted work can be lost or redelivered after a crash. Use a committed event when the work must survive a crash. |
| Notices | Notify subscribers. Each subscribed replica can receive the notice. |
| Schedules | Trigger work at a time or on a recurrence. A received Fitz firing can be lost on process failure. |

See [Fitz integration](docs/fitz.md) for delivery and persistence details.

## Development

Run a service-free check from the repository root with the .NET 11 SDK.

```sh
dotnet test --solution Portia.slnx -c Release \
  --filter-not-trait Category=BrokerIntegration --filter-not-trait Category=StorageIntegration \
  --filter-not-trait Category=CassieIntegration --filter-not-trait Category=DurabilityIntegration
```

See [Contributing](CONTRIBUTING.md) for hosted integration checks and formatting,
and the [changelog](CHANGELOG.md) for release history.

Portia is licensed under [Apache 2.0](LICENSE).

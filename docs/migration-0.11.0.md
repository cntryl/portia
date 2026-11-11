# Migrating to Portia 0.11.0

Portia 0.11.0 targets .NET 11. Upgrade the application runtime and keep the complete
`Cntryl.Portia.*` package family on 0.11.0. Repository development requires the stable 11.0.100
SDK or a later .NET 11 feature band selected by `global.json`.

Applications upgrading from older processor contracts must also follow the
[0.10.0 migration notes](migration-0.10.0.md).

## Dependencies

Portia uses Fitz .NET 4.1.0 and requires Fitz server 0.4.0 / Wire 3. A deployment already using
that broker generation can retain it when upgrading the .NET clients. For a Wire 1 or Wire 2
deployment, follow the coordinated [Fitz 4 upgrade guide](fitz-4-upgrade.md).

The dependency versions include:

| Package family | Version |
| --- | --- |
| Microsoft Extensions and ASP.NET Core | 11.0.0 |
| Microsoft.OpenApi and Microsoft.OpenApi.YamlReader | 3.11.0 |
| ModelContextProtocol | 2.4.0 |
| OpenTelemetry exporters and hosting | 1.21.0 |
| OpenTelemetry.Instrumentation.AspNetCore in the Accounts example | 1.20.2 |
| AWSSDK.S3 | 4.0.104.12 |
| Microsoft.NET.Test.Sdk | 18.11.1 |

Update conflicting direct references or central package versions instead of suppressing downgrade
diagnostics. Microsoft.OpenApi 3.x changes some object-model properties to interface collections;
application-owned OpenAPI transformers must compile against that API.

## JSON and OpenAPI contracts

The updated OpenAPI library emits version `3.1.2` for Portia's OpenAPI 3.1 documents. JSON and
YAML documents retain the same schema and endpoint contracts.

.NET 11 source-generated JSON deserialization preserves an `init` property's declared initializer
when the payload omits that property. MCP binding and input schemas accept optional initialized
properties accordingly; required constructor members and explicit null restrictions remain enforced.

## Remote outcomes and stable runtime improvements

`RemoteRequestOutcome` and `RemoteRequestOutcome<TOut>` use C# 15 closed hierarchies. Consumers
can exhaustively match their four cases: `Completed`, `NotDispatched`, `CancellationForwarded`,
and `OutcomeUnknown`. Existing case payloads and `Kind` values retain their meaning. A completed
request may contain a business rejection; cancellation forwarding does not establish that effects
were rolled back, and an unknown outcome still requires reconciliation.

MCP snapshots parse directly into owned JSON elements. Referenced JSON-context discovery reuses
unchanged registrations, and processor dispatchers emit their layout directly. See the bounded
[performance measurements](performance-and-scaling.md#stable-net-11-follow-up) for allocation
results and measurement limits. This release enables no experimental runtime features.

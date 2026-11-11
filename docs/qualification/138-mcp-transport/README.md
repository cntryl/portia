# MCP application and transport qualification (#138)

This record covers Portia's default stateless Streamable HTTP mode and stdio using the stable
ModelContextProtocol 2.4.0 package. It is not a qualification of optional stateful Streamable HTTP
sessions.

## What the tests exercise

- `TransportParityTests` runs the same application operations through direct dispatch, REST,
  stateless MCP HTTP, and an external MCP stdio process. It checks authorization, guards, pipeline
  behaviors, committed event effects, tenant isolation, expected failures, concurrency, and unique
  asynchronously disposed handler scopes.
- `McpLifecycleQualificationTests` verifies that caller cancellation reaches tool, resource, and
  prompt handlers and disposes their scopes; deadline failures are sanitized. Stdio automatic
  cancellation is checked at protocol revisions `2025-11-25` and `2026-07-28`, including the actual
  `notifications/cancelled` frame observed by a minimal SDK-only server.
- `McpTelemetryQualificationTests` uses in-memory OpenTelemetry exporters to inspect spans, metrics,
  and fault logs for successful and failed tool, resource, and prompt operations. It checks bounded
  tags and verifies that sensitive actor and resource values are absent from telemetry.
- Existing MCP registration and primitive tests cover visibility and guessed lookups, HTTP actor
  authority, ingress/result limits, malformed inputs, protocol stdout cleanliness, and cancellation
  controls.

## Implementation changes

Stateless HTTP now retains a fresh service scope for each MCP operation while preserving the
application's `ConfigureSessionOptions` callback. Resource reads and prompt gets propagate caller
cancellation, record bounded process telemetry correlated to request execution, and keep technical
fault logs separate from expected request failures. The SDK-only stdio regression demonstrates that
2.4.0 sends cancellation notifications and cancels server handlers for both tested protocol revisions.

## Remaining upstream limitation

The MCP SDK's stdio cancellation fix ([PR #1917](https://github.com/modelcontextprotocol/csharp-sdk/pull/1917))
is included in the stable 2.4.0 release. Stateful Streamable HTTP cancellation still depends on
[upstream PR #1377](https://github.com/modelcontextprotocol/csharp-sdk/pull/1377), which remains open
and explicitly leaves that transport path unresolved. Portia's optional HTTP configuration callback
can select stateful mode, so issue #138 remains open until that behavior has an authoritative outcome
and a real cancellation regression passes.

## Local verification

On this source revision:

- `dotnet test test/Portia.McpTests/Portia.McpTests.csproj -c Release --no-restore` — 152 passed,
  zero skipped.
- `dotnet format Portia.slnx --verify-no-changes --no-restore --include ...` — passed. The workspace
  loader emitted its existing non-fatal warnings.
- `git diff --check` — passed.

Hosted exact-head CI remains a separate release gate.

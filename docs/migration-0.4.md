# Migrating to Portia 0.4

Portia 0.4 makes JSON metadata ownership reusable across assemblies, makes tenant workload patterns
explicit, and adds an optional real-client MCP testing package. It does not change request, event,
HTTP, MCP, or durable wire formats.

## JSON contexts

`PORTIA025` is now reported at actual registration and transport use sites instead of for every
declared request handler. A `[PortiaJsonContext]` advertises each of its `[JsonSerializable]` roots
to referencing assemblies, so an application should remove duplicate roots already owned by a
referenced domain assembly.

The generated `AddPortia()` registration composes those referenced contexts at runtime. The startup
check remains authoritative: advertising a root does not make it usable unless the assembly owning
the context is registered through `AddPortia()`.

## Per-tenant workloads

Replace placeholder realms used by per-tenant projectors and reactors:

```csharp
// Before
EventStreamPattern.ForPattern("tenant", "accounts")

// Portia 0.4
EventStreamPattern.ForTenant("accounts")
```

`WorkloadScope.PerTenant` now requires `ForTenant(...)`; `WorkloadScope.Global` requires an exact
`ForPattern(...)`. Portia rejects mismatches during startup and rejects manually running an unbound
tenant template. A hosted per-tenant workload exposes the exact tenant realm after binding.

## MCP consumer tests

Reference `Cntryl.Portia.Mcp.Testing` and connect `McpScenario` with the `HttpClient` used by the
application test. Authentication belongs on that client so the real ASP.NET authorization and
Portia actor-resolution path remains under test. The scenario does not dispose the supplied client.

```csharp
await using var scenario = await McpScenario.ConnectAsync(client, new Uri("/mcp", UriKind.Relative));
var tools = await scenario.ListTools().ExpectExactly("accounts.get", "accounts.list");
var call = await scenario.When("accounts.get", new Dictionary<string, object?> { ["id"] = "42" })
    .ExpectSuccess();
```

The package depends on neither TestHost nor an assertion framework, so the same API works with a
`WebApplicationFactory` client or a remote Streamable HTTP endpoint.

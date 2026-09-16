# Model Context Protocol

Portia exposes deliberately selected application requests as Model Context Protocol tools. MCP is
an ingress adapter over `IRequestBus`: tool calls use the same handlers, authorization, pipeline
behaviors, request context, result contracts, and event production as direct and HTTP dispatch.
It is not a second application model and does not route directly through Fitz or a read-model store.

## Declare tools with the application

Tool declarations belong in the shared application setup beside their handlers:

```csharp
return services.AddPortia()
    .AddRequestHandler<GetAccountHandler>()
    .AddRequestHandler<DepositAccountHandler>()
    .AddMcpTool<GetAccount>(tool => tool.ReadOnly())
    .AddMcpTool<DepositAccount>(tool => tool
        .DescribedAs("Deposits funds into an account.")
        .Destructive());
```

`AddMcpTool<TRequest>()` requires `IRequest` or `IRequest<T>` plus `ICallable`. The existing
`ICallable` marker remains the single opt-in for remote request-response ingress. Declaring a tool
does not start a server, so the same shared setup remains safe in API, worker, test, and migration
hosts.

By default, the request's `Discriminator` name is the MCP tool name and its XML summary is the
model-facing description. The request's existing Portia JSON contract is the complete input schema.
`RequestRoute` stays transport-routing metadata and does not add synthetic MCP arguments.
When a referenced contracts assembly does not publish XML documentation, Portia supplies a stable
`Invokes the {RequestType} request.` fallback; use `DescribedAs(...)` when that fallback is not
specific enough for a model to choose the tool confidently.

Presentation metadata can be refined without changing the domain request:

```csharp
.AddMcpTool<DepositAccount>(tool => tool
    .Named("accounts.deposit")
    .Titled("Deposit funds")
    .DescribedAs("Deposits an amount into an existing account.")
    .Destructive()
    .Idempotent())
```

MCP annotations are client hints, never authorization or execution policy.

## Streamable HTTP

Reference `Cntryl.Portia.Mcp.AspNetCore`, build the same application, and map one endpoint:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAccountsApplication(builder.Configuration)
    .AddMcpHttp();

var app = builder.Build();
app.MapPortiaMcp().RequireAuthorization("agents");
app.Run();
```

The default endpoint is `/mcp` and the default HTTP mode is stateless Streamable HTTP. The optional
`AddMcpHttp(options => ...)` callback can select another SDK-supported session mode without Portia
overwriting it later during `Build()`. Standard
ASP.NET Core endpoint conventions remain authoritative for authentication, authorization, host
filtering, and CORS. Portia uses the authenticated `HttpContext.User`; MCP arguments and metadata
cannot replace that principal. Authorization still runs inside `IRequestBus` on every invocation.
The endpoint also honors `PortiaHttpOptions.MaxJsonBodyBytes`. As with generated HTTP endpoints, a
`POST` or `DELETE` from another browser origin returns 403 unless the CORS pipeline allows that
origin; see [cross-origin requests](getting-started.md#cross-origin-requests).

## Standard input and output

Reference `Cntryl.Portia.Mcp`, register an `IMcpActorProvider`, and activate stdio:

```csharp
builder.Services.AddAccountsApplication(builder.Configuration)
    .AddMcpStdio(options => options.UseLocalDevelopmentActor());
await builder.Build().RunAsync();
```

Stdio has no authenticated HTTP principal, so actor selection is explicit and fails closed when no
provider is registered. `UseLocalDevelopmentActor()` is intentionally named and documented as a
single-user development convenience; production hosts use `UseActorProvider<TProvider>()`. Protocol
frames use standard output; `AddMcpStdio()` routes the built-in console logger to standard error so
hosting diagnostics cannot corrupt the protocol stream.

## Results and failures

`IRequest<T>` successes return an object-shaped `{ "result": T }` as MCP structured content and as
compact JSON text, keeping the result valid for every supported MCP protocol revision. A successful
`IRequest` returns a short success message. Expected Portia failures set MCP `isError` and include a
structured `kind`, `message`, and `isTransient` object so a model can correct inputs or decide whether
retrying is appropriate. Binding, missing-actor, and unexpected failures use stable `Binding`,
`Unauthorized`, and `Internal` envelopes. Portia does not disclose stack traces, claims, tokens,
routes, exception messages, or internal event data.

## Real consumer tests

Reference `Cntryl.Portia.Mcp.Testing` and connect through the same authenticated `HttpClient` your
application test owns:

```csharp
await using var mcp = await McpScenario.ConnectAsync(client, new Uri("http://localhost/mcp"));
var tools = await mcp.ListTools().ExpectExactly("accounts.get", "accounts.deposit");
var result = await mcp.When("accounts.get", new Dictionary<string, object?>
{
    ["account_id"] = accountId
}).ExpectSuccess();
```

The scenario owns the official MCP client and transport but never disposes `client`. Catalog and
call snapshots contain detached schemas, hints, metadata, text, and structured JSON for ordinary
assertions. `ExpectFailure(kind)` checks Portia's structured failure kind. Authentication is not a
scenario shortcut: configure it on the supplied HTTP client so ASP.NET authorization and Portia
actor resolution stay inside the test.

## Deliberate first-release boundary

The supported surface is unary tools. Portia does not currently map MCP resources, prompts,
sampling, elicitation, `IStreamRequest<T>`, queues, notices, schedules, or durable MCP tasks. Those
concepts have different ownership and lifecycle semantics and will only be added behind explicit
application contracts rather than inferred from existing transports.

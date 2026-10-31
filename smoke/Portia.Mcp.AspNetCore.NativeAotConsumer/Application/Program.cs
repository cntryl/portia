using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using Cntryl.Portia;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSingleton<IPortiaOperations>(new PortiaOperations(new EmptyOperationalStore(),
    new OperationalOptions(), new EmptyOperationalTopology(), new OperationalReadPolicy()));
builder.WebHost.UseUrls("http://127.0.0.1:0");
_ = builder.Services.AddPortia()
    .AddRequestHandler<ReadGreetingHandler>()
    .AddMcpTool<ReadGreeting>(tool => tool.ReadOnly())
    .AddMcpResource<ReadGreeting, string>("greetings://local/{name}",
        values => new ReadGreeting(values["name"]),
        options => { options.Authenticated(); options.AsText("text/plain", value => value); })
    .AddMcpPrompt<ReadGreeting, string>("greeting",
        values => new ReadGreeting(values["name"]),
        value => [new McpPromptMessage("user", value)],
        options => { options.Authenticated(); options.Required("name"); })
    .AddMcpHttp().AddAdminMcpHttp();
var app = builder.Build();
app.Use((context, next) =>
{
    context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "native-aot-test-actor")], "smoke"));
    return next(context);
});
_ = app.MapPortiaMcp().AllowAnonymous();
_ = app.MapPortiaAdminMcp().AllowAnonymous();
_ = app.MapPortiaRepairApprovals().AllowAnonymous();
await app.StartAsync();

using var http = new HttpClient();
await using var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri(new Uri(app.Urls.Single()), "/mcp")
}, http, NullLoggerFactory.Instance, false);
await using var client = await McpClient.CreateAsync(transport);
var tools = await client.ListToolsAsync();
if (!tools.Any(tool => tool.Name == "greetings.read"))
    throw new InvalidOperationException("The packed native MCP server did not expose greetings.read.");
var result = await client.CallToolAsync("greetings.read", new Dictionary<string, object?>
{
    ["name"] = "native-aot"
}!);
if (result.IsError == true || result.StructuredContent?.GetProperty("result").GetString() != "Hello, native-aot.")
    throw new InvalidOperationException($"Unexpected MCP result: {result.StructuredContent?.GetRawText()}");
var resource = await client.ReadResourceAsync("greetings://local/native-aot");
if (resource.Contents.Single() is not ModelContextProtocol.Protocol.TextResourceContents resourceText ||
    resourceText.Text != "Hello, native-aot.")
    throw new InvalidOperationException("Unexpected MCP resource result.");
var prompt = await client.GetPromptAsync("greeting", new Dictionary<string, object?> { ["name"] = "native-aot" });
if (prompt.Messages.Single().Content is not ModelContextProtocol.Protocol.TextContentBlock promptText ||
    promptText.Text != "Hello, native-aot.")
    throw new InvalidOperationException("Unexpected MCP prompt result.");
await using var adminTransport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri(new Uri(app.Urls.Single()), "/admin/mcp")
}, http, NullLoggerFactory.Instance, false);
await using var admin = await McpClient.CreateAsync(adminTransport);
if ((await admin.ListToolsAsync()).Count != 8)
    throw new InvalidOperationException("The native administration catalog was not isolated.");
using var globalScope = JsonDocument.Parse("{\"tenant\":null}");
var issues = await admin.CallToolAsync("list_issues", new Dictionary<string, object?>
{
    ["scope"] = globalScope.RootElement.Clone()
});
if (issues.IsError == true)
    throw new InvalidOperationException("The native administrative scoped read failed.");
await app.StopAsync();

/// <summary>Reads a greeting without changing application state.</summary>
[Discriminator("greetings.read")]
[RequestRoute("public", "greetings", "message", "read")]
sealed record ReadGreeting(string Name) : IRequest<string>, ICallable;

sealed class ReadGreetingHandler : IRequestHandler<ReadGreeting, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<ReadGreeting> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success($"Hello, {context.Request.Name}."));
}

[PortiaJsonContext]
[JsonSerializable(typeof(ReadGreeting))]
[JsonSerializable(typeof(string))]
sealed partial class SmokeJsonContext : JsonSerializerContext;

sealed class EmptyOperationalTopology : IApplicationTopology
{
    public ApplicationTopology Describe() => new([], [], "opaque dependencies unknown");
}
sealed class EmptyOperationalStore : IOperationalStore
{
    public ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default)
        => ValueTask.FromResult<OperationalProcessorState?>(null);
    public ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<OperationalProcessorState>>([]);
    public ValueTask<bool> CompareExchangeAsync(OperationalProcessorState replacement, long expectedRevision,
        Guid expectedOwner, CancellationToken ct = default) => throw new NotSupportedException();
}
sealed class OperationalReadPolicy : IOperationalAuthorizer
{
    public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal actor, OperationalScope scope, OperationalAccess permission,
        CancellationToken ct = default) => ValueTask.FromResult(scope == OperationalScope.Global && permission == OperationalAccess.Read);
}

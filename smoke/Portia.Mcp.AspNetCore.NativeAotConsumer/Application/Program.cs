using System.Text.Json.Serialization;
using System.Security.Claims;
using Cntryl.Portia;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

var builder = WebApplication.CreateSlimBuilder(args);
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
    .AddMcpHttp();
var app = builder.Build();
app.Use((context, next) =>
{
    context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "native-aot-test-actor")], "smoke"));
    return next(context);
});
_ = app.MapPortiaMcp().AllowAnonymous();
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

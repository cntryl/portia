using System.Text.Json.Serialization;
using Cntryl.Portia;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
_ = builder.Services.AddPortia()
    .AddRequestHandler<ReadGreetingHandler>()
    .AddMcpTool<ReadGreeting>(tool => tool.ReadOnly());
var app = builder.Build();
_ = app.MapPortiaMcp();
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
if (result.IsError == true || result.StructuredContent?.GetRawText() != "\"Hello, native-aot.\"")
    throw new InvalidOperationException($"Unexpected MCP result: {result.StructuredContent?.GetRawText()}");
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

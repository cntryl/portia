using System.Security.Claims;
using System.Text.Json.Serialization;
using Cntryl.Portia;
using Cntryl.Portia.Testing;

// A packed MCP server on a loopback port, driven through the packed McpScenario the way an
// application's own consumer test drives its endpoint.
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
_ = builder.Services.AddPortia()
    .AddRequestHandler<ReadGreetingHandler>()
    .AddMcpTool<ReadGreeting>(tool => tool.ReadOnly())
    .AddMcpHttp();
await using var app = builder.Build();
app.Use((context, next) =>
{
    context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "mcp-testing-smoke-actor")], "smoke"));
    return next(context);
});
_ = app.MapPortiaMcp();
await app.StartAsync();

using var http = new HttpClient();
await using (var mcp = await McpScenario.ConnectAsync(http, new Uri(new Uri(app.Urls.Single()), "/mcp")))
{
    _ = await mcp.ListTools().ExpectExactly("greetings.read");
    var result = await mcp.When("greetings.read", new Dictionary<string, object?>
    {
        ["name"] = "package"
    }).ExpectSuccess();
    if (result.StructuredJson?.GetProperty("result").GetString() != "Hello, package.")
        throw new InvalidOperationException($"Unexpected MCP result: {result.StructuredJson?.GetRawText()}");
}

await app.StopAsync();
Console.WriteLine("MCP testing package consumer passed.");

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

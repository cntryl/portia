using System.Text.Json;
using Cntryl.Portia;
using Microsoft.Extensions.Hosting;

if (Environment.GetEnvironmentVariable("PORTIA_MCP_TEST_PROCESS_ID_FILE") is { Length: > 0 } processIdFile)
    File.WriteAllText(processIdFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

if (args.Contains("--hang-before-mcp", StringComparer.Ordinal))
{
    Console.Error.WriteLine(Environment.ProcessId);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args.Contains("--fail-initialization", StringComparer.Ordinal))
{
    Console.Error.WriteLine(Environment.ProcessId);
    while (Console.ReadLine() is { } request)
    {
        using var message = JsonDocument.Parse(request);
        if (!message.RootElement.TryGetProperty("id", out var id))
            continue;

        Console.Out.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText()
            + ",\"error\":{\"code\":-32601,\"message\":\"Intentional initialization failure.\"}}");
        Console.Out.Flush();
    }

    // Keep the child alive after the client closes its input so the test observes transport cleanup.
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args.Contains("--report-process-id", StringComparer.Ordinal))
    Console.Error.WriteLine(Environment.ProcessId);

var builder = Host.CreateApplicationBuilder([]);
var application = builder.Services.AddPortia().AddRequestHandler<StdioGreetingHandler>();
if (args.Contains("--resource-only", StringComparer.Ordinal))
    _ = application.AddMcpResource<StdioGreeting, string>("greetings://stdio/{name}",
        values => new StdioGreeting(values["name"]),
        options => { options.Authenticated(); options.AsText("text/plain", value => value); });
else if (args.Contains("--prompt-only", StringComparer.Ordinal))
    _ = application.AddMcpPrompt<StdioGreeting, string>("stdio-greeting",
        values => new StdioGreeting(values["name"]),
        value => [new McpPromptMessage("user", value)],
        options => { options.Authenticated(); options.Required("name"); });
else
    _ = application.AddMcpTool<StdioGreeting>();
_ = application.AddMcpStdio(options => options.UseLocalDevelopmentActor("stdio-test-actor"));
await builder.Build().RunAsync();

namespace Cntryl.Portia
{
}

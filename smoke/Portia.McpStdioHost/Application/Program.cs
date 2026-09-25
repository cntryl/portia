using Cntryl.Portia;
using Microsoft.Extensions.Hosting;

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

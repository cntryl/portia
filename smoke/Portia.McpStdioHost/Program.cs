using Cntryl.Portia;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
_ = builder.Services.AddPortia()
    .AddRequestHandler<StdioGreetingHandler>()
    .AddMcpTool<StdioGreeting>()
    .AddMcpStdio(options => options.UseLocalDevelopmentActor("stdio-test-actor"));
await builder.Build().RunAsync();

namespace Cntryl.Portia
{
}

using System.Text.Json.Serialization;
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
    /// <summary>Reads a greeting and the explicitly configured stdio actor.</summary>
    [Discriminator("stdio.greetings.read")]
    sealed record StdioGreeting(string Name) : IRequest<string>, ICallable;

    sealed class StdioGreetingHandler : IRequestHandler<StdioGreeting, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<StdioGreeting> context, CancellationToken ct) =>
            ValueTask.FromResult(Result<string>.Success(
                $"Hello, {context.Request.Name}, from {context.Actor.Identity?.Name}."));
    }

    [PortiaJsonContext]
    [JsonSerializable(typeof(StdioGreeting))]
    [JsonSerializable(typeof(string))]
    sealed partial class StdioJsonContext : JsonSerializerContext;
}

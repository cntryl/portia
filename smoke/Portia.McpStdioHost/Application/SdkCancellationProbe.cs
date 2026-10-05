using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cntryl.Portia;

// A control server using only SDK 2.2.0, without Portia dispatch, deadlines, or telemetry.
static class SdkCancellationProbe
{
    internal static async Task RunAsync(HostApplicationBuilder builder, string directory)
    {
        Directory.CreateDirectory(directory);
        builder.Logging.ClearProviders();
        builder.Services.AddScoped(_ => new ProbeScope(directory));
        _ = builder.Services.AddMcpServer().WithStdioServerTransport()
            .WithCallToolHandler(async (call, ct) => { await WaitAsync(call, ct); return new CallToolResult(); })
            .WithReadResourceHandler(async (call, ct) => { await WaitAsync(call, ct); return new ReadResourceResult(); })
            .WithGetPromptHandler(async (call, ct) => { await WaitAsync(call, ct); return new GetPromptResult { Messages = [] }; });
        var gate = new object();
        builder.Services.Configure<McpServerOptions>(options => options.Filters.Message.IncomingFilters.Add(next => async (message, ct) =>
        {
            var rpc = message.JsonRpcMessage;
            var line = rpc is JsonRpcRequest request ? $"request\t{request.Id}\t{request.Method}"
                : rpc is JsonRpcNotification notification ? $"notification\t{notification.Method}\t{notification.Params?.ToJsonString()}" : rpc.GetType().Name;
            lock (gate)
                File.AppendAllText(Path.Combine(directory, "ingress.tsv"), line + "\n");
            await next(message, ct);
        }));
        await builder.Build().RunAsync();

        async Task WaitAsync(MessageContext call, CancellationToken ct)
        {
            _ = call.Services!.GetRequiredService<ProbeScope>();
            await File.WriteAllTextAsync(Path.Combine(directory, "started"), "started", ct);
            try
            { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "canceled"), "canceled", CancellationToken.None);
                throw;
            }
        }
    }

    sealed class ProbeScope(string directory) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await File.WriteAllTextAsync(Path.Combine(directory, "disposed"), "disposed", CancellationToken.None);
    }
}

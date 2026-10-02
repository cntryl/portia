using System.Security.Claims;
using Cntryl.Portia;
using Cntryl.Portia.McpContracts;
using Cntryl.Portia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder([]);
if (args.Contains("--sdk-cancellation", StringComparer.Ordinal))
{
    await SdkCancellationProbe.RunAsync(builder, args[Array.IndexOf(args, "--evidence-dir") + 1]);
    return;
}

if (args.Contains("--qualification", StringComparer.Ordinal))
{
    var evidenceIndex = Array.IndexOf(args, "--evidence-dir");
    var evidence = evidenceIndex >= 0 ? new QualificationTelemetry(args[evidenceIndex + 1]) : null;
    evidence?.Configure(builder.Services);
    var ingressGate = new object();
    if (evidenceIndex >= 0)
        builder.Services.Configure<McpServerOptions>(options => options.Filters.Message.IncomingFilters.Add(next => async (message, ct) =>
        {
            var rpc = message.JsonRpcMessage;
            var line = rpc is JsonRpcRequest request ? $"request\t{request.Id}\t{request.Method}\t{request.Params?.ToJsonString()}"
                : rpc is JsonRpcNotification notification ? $"notification\t{notification.Method}\t{notification.Params?.ToJsonString()}" : rpc.GetType().Name;
            lock (ingressGate)
                File.AppendAllText(Path.Combine(args[evidenceIndex + 1], "ingress.tsv"), line + "\n");
            await next(message, ct);
        }));
    _ = builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
    var qualification = QualifiedApplication.Add(builder.Services)
        .AddMcpTool<QualifiedChange>(tool => tool.ReadOnly().Idempotent())
        .AddMcpTool<QualifiedRead>()
        .AddMcpResource<QualifiedRead, QualifiedState>("qualification://values/{tenant}",
            values => new QualifiedRead(values["tenant"]), options => { options.Authenticated(); options.AsJson(); })
        .AddMcpResource<QualifiedRead, QualifiedState>("qualification://wait/{tenant}",
            values => new QualifiedRead(values["tenant"], Wait: true), options => { options.Authenticated(); options.AsJson(); })
        .AddMcpResource<QualifiedRead, QualifiedState>("qualification://failure/{tenant}",
            values => new QualifiedRead(values["tenant"], Fail: true), options => { options.Authenticated(); options.AsJson(); })
        .AddMcpPrompt<QualifiedRead, QualifiedState>("qualification-read", values => new QualifiedRead(values["tenant"]),
            value => [new McpPromptMessage("user", string.Join(',', value.Values))],
            options => { options.Authenticated(); options.Required("tenant"); })
        .AddMcpPrompt<QualifiedRead, QualifiedState>("qualification-wait", values => new QualifiedRead(values["tenant"], Wait: true),
            _ => [new McpPromptMessage("user", "completed")],
            options => { options.Authenticated(); options.Required("tenant"); })
        .AddMcpPrompt<QualifiedRead, QualifiedState>("qualification-failure", values => new QualifiedRead(values["tenant"], Fail: true),
            _ => [new McpPromptMessage("user", "completed")],
            options => { options.Authenticated(); options.Required("tenant"); })
        .AddMcpStdio(options => options.UseActorProvider<QualificationActorProvider>());
    if (args.Contains("--short-deadline", StringComparer.Ordinal))
        _ = qualification.ConfigureMcpLimits(options => options.OperationDeadline = TimeSpan.FromMilliseconds(250));
    using var host = builder.Build();
    using var snapshots = new CancellationTokenSource();
    var export = evidence?.RunAsync(host.Services, snapshots.Token) ?? Task.CompletedTask;
    try
    { await host.RunAsync(); }
    finally
    {
        await snapshots.CancelAsync();
        try
        { await export; }
        catch (OperationCanceledException) when (snapshots.IsCancellationRequested) { }
    }
    return;
}
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
    sealed class QualificationActorProvider : IMcpActorProvider
    {
        public ValueTask<ClaimsPrincipal> GetActorAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, QualifiedObservations.Tenant),
                    new Claim(ClaimTypes.NameIdentifier, QualifiedObservations.Tenant, ClaimValueTypes.String, "qualification")],
                "qualification")));
    }
}

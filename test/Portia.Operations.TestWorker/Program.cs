using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Cntryl.Fitz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

// Child process controlled by OperationalDurabilityIntegrationTests against its isolated broker.
static partial class Program
{
    const string Realm = "portia-durability";
    const string Route = "kv://qualification/portia/checkpoints";
    static readonly EventStreamPattern Pattern = EventStreamPattern.ForPattern(Realm, "source");
    static readonly OperationalTarget Target = new("durability-reactor", Pattern.ToString(), OperationalScope.Global);

    static async Task Main(string[] args)
    {
        var mode = args[0];
        var signal = args.Length > 1 ? args[1] : null;
        await using var client = new Client(new ClientConfig(new Uri(
            Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT") ?? "ws://127.0.0.1:24090/ws")));
        await client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(20)));
        var events = new FitzEventStore(client.Stream, new JsonDomainEventSerializer(new DomainEventTypeCatalog()
            .Register<Changed>(1, "portia.durability.changed"), [], DurabilityJsonContext.Default.Options));
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEventStore>(events);
        builder.Services.AddSingleton(new Control(mode, signal));
        builder.Services.AddSingleton<IOperationalAuthorizer, Policy>();
        builder.Services.AddPortia().UseFitzClient(client, fitz => fitz.DisableRequestWorkers()
                .UseFleet(new FleetRunOptions { MembershipSelector = "lease://qualification/portia-members/*" }).UseOperations(Route,
                options => options.QuiescedWorkerCutover = true))
            .AddReactor<DurabilityReactor>(Target.Processor, WorkloadScope.Global,
                options => options.PollInterval = TimeSpan.FromMilliseconds(20)).AddWorkers();
        using var host = builder.Build();
        await host.StartAsync();
        var operations = host.Services.GetRequiredService<IPortiaOperations>();
        var actor = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "qualification-operator")], "qualification"));
        if (mode is "blocked" or "crash")
        {
            await UntilAsync(async () => (await operations.InspectProcessorAsync(actor, Target))?.Block is not null);
            if (mode == "blocked")
                await File.WriteAllTextAsync(signal!, "durable block");
            else
            {
                var plan = await operations.PreviewRepairAsync(actor, Target);
                _ = await operations.ApproveRepairAsync(actor, Target, plan.Id);
                _ = await operations.ExecuteRepairAsync(actor, Target, plan.Id);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        else if (mode == "recover")
        {
            await UntilAsync(async () => (await operations.InspectProcessorAsync(actor, Target))?.Attempts.Any(a => a.Outcome == "committed") == true);
            await File.WriteAllTextAsync(signal!, "recovery checkpoint committed");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        else
            throw new ArgumentException("Unknown qualification mode.");
        await host.StopAsync();
    }

    static async Task UntilAsync(Func<Task<bool>> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!await ready())
            await Task.Delay(20, deadline.Token);
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal sealed record Control(string Mode, string? Signal);
    [Discriminator("portia.durability.changed")]
    internal sealed record Changed(int Value) : DomainEvent;

    internal sealed partial class DurabilityReactor(IProjectionCheckpointStore checkpoints, IEventStore events, Control control)
        : BatchReactor(checkpoints, Program.Pattern), IReactorHandler<Changed>
    {
        public async ValueTask<Result> HandleAsync(IReactorContext<Changed> context, CancellationToken ct)
        {
            if (control.Mode == "blocked" && context.Trigger.Value == 2)
                return Result.Failure(RequestError.Validation("qualification failure"));
            var id = CreateEffectId(context, "effect");
            var stream = new EventStreamAddress(Realm, "effects", id.ToString());
            var exists = false;
            await foreach (var existing in events.ReadAsync(stream, 0, ct))
            {
                Require(((Changed)existing.Event).Value == context.Trigger.Value, "Stable effect identity changed content.");
                exists = true;
            }
            if (!exists)
            {
                var effect = new Changed(context.Trigger.Value);
                effect.AttachMetadata(new DomainEventMetadata(id, id, 1, context.StartedAt));
                await events.AppendAsync(stream, 0, [effect], ct);
            }
            if (control.Mode == "crash" && context.Trigger.Value == 1)
            {
                await File.WriteAllTextAsync(control.Signal!, "effect observed; checkpoint uncommitted", ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return Result.Success;
        }
    }

    sealed class Policy : IOperationalAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal actor, OperationalScope scope, OperationalAccess permission,
            CancellationToken ct = default) => ValueTask.FromResult(scope == OperationalScope.Global && actor.Identity?.IsAuthenticated == true);
    }
}

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Program.Changed))]
sealed partial class DurabilityJsonContext : JsonSerializerContext;

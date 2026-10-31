using Microsoft.Extensions.Hosting;
using AccountProjector = Cntryl.Portia.CassieProjectionIntegrationTests.AccountProjector;
using AccountRepository = Cntryl.Portia.CassieProjectionIntegrationTests.AccountRepository;
using Database = Cntryl.Portia.CassieProjectionIntegrationTests.Database;

namespace Cntryl.Portia;

/// <summary>Qualifies the operator journey through real Fitz effects, worker ownership, and Cassie projection.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "CassieIntegration")]
public sealed partial class OperationalJourneyIntegrationTests(FitzBrokerFixture broker)
{
    /// <summary>A blocked reactor survives worker restart, recovers exactly once, and its committed effects project.</summary>
    [Fact]
    public async Task ShouldDiagnoseApproveRetryAndVerifyRealProcessing()
    {
        await using var client = await broker.CreateClientAsync();
        var events = new FitzEventStore(client.Stream, TestJson.DomainSerializer(new DomainEventTypeCatalog()
            .Register<ValueChanged>(1, "test.value.changed")));
        var realm = "operations-" + Uuid.CreateVersion4();
        var control = new JourneyControl(EventStreamPattern.ForPattern(realm, "source"));
        var target = new OperationalTarget("journey", control.Pattern.ToString(), OperationalScope.Global);
        var route = $"kv://{realm}/portia/checkpoints";
        var aggregate = Uuid.CreateVersion4();
        DomainEvent[] source = [new ValueChanged(100), new ValueChanged(200)];
        for (var i = 0; i < source.Length; i++)
            source[i].AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregate, (ulong)i + 1, DateTimeOffset.UtcNow));
        await events.AppendAsync(new EventStreamAddress(realm, "source", aggregate.ToString()), 0, source);

        Guid previousOwner;
        await using (var provider = Services(client, events, control, route).BuildServiceProvider())
        {
            var worker = provider.GetServices<IHostedService>().OfType<PortiaWorkloadService>().Single();
            await worker.StartAsync(default);
            try
            {
                var journal = provider.GetRequiredService<IOperationalStore>();
                await WaitAsync(async () => (await journal.ReadAsync(target))?.Block is not null);
                await control.IndependentProgress.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(worker.ExecuteTask!.IsCompleted);
                var issue = Assert.Single(await provider.GetRequiredService<IPortiaOperations>()
                    .ListIssuesAsync(TestOperationalAuthorizer.Actor(), OperationalScope.Global));
                previousOwner = issue.OwnerSession;
                Assert.Null(control.LastException);
                Assert.Equal(2, Assert.Single(issue.Attempts).Sources.Count);
                Assert.Equal(2, control.Invocations);
                Assert.Single(await ReadEffectsAsync(events, realm));
            }
            finally { await worker.StopAsync(default); worker.Dispose(); }
        }

        await using (var provider = Services(client, events, control, route).BuildServiceProvider())
        {
            var worker = provider.GetServices<IHostedService>().OfType<PortiaWorkloadService>().Single();
            await worker.StartAsync(default);
            try
            {
                var operations = provider.GetRequiredService<IPortiaOperations>();
                await WaitAsync(async () => await operations.InspectProcessorAsync(TestOperationalAuthorizer.Actor(), target)
                    is { Block: not null } state && state.OwnerSession != previousOwner);
                Assert.Equal(2, control.Invocations);
                var agent = TestOperationalAuthorizer.Actor();
                var plan = await operations.PreviewRepairAsync(agent, target);
                _ = await operations.ApproveRepairAsync(TestOperationalAuthorizer.Actor(true), target, plan.Id);
                control.Fail = false;
                _ = await operations.ExecuteRepairAsync(agent, target, plan.Id);
                await WaitAsync(async () => (await operations.VerifyRepairAsync(agent, target, plan.Id)).Status == "committed");
                var verification = await operations.VerifyRepairAsync(agent, target, plan.Id);
                var checkpoint = await provider.GetRequiredService<IProjectionCheckpointStore>().LoadAsync(target.Identity);
                Assert.Equal(checkpoint.Cursor.Value, verification.Checkpoint);
                Assert.NotEqual(ProjectionCheckpoint.Start, checkpoint);
                Assert.Equal(4, control.Invocations);
                Assert.Equal(2, (await ReadEffectsAsync(events, realm)).Count);
                Assert.Null((await operations.InspectProcessorAsync(agent, target))!.Block);
            }
            finally { await worker.StopAsync(default); worker.Dispose(); }
        }

        await using var database = await Database.OpenAsync();
        var identity = new CheckpointIdentity("AccountProjector", EventStreamPattern.ForPattern(realm, "accounts"));
        await using var accounts = new AccountRepository(database);
        _ = await new ProjectorRunner(events).RunAsync(new AccountProjector(accounts, identity.Pattern),
            await accounts.LoadCheckpointAsync(identity));
        var committedEffects = await ReadEffectsAsync(events, realm);
        foreach (var effect in committedEffects)
        {
            var model = await accounts.GetAsync(identity, effect.Event.Metadata.AggregateId.ToString());
            Assert.Equal(((ValueChanged)effect.Event).Value, model!.BalanceCents);
        }
    }

    static ServiceCollection Services(Client client, IEventStore events, JourneyControl control, string route)
    {
        var services = new ServiceCollection();
        services.AddSingleton(events);
        services.AddSingleton(control);
        services.AddSingleton<IOperationalAuthorizer, TestOperationalAuthorizer>();
        services.AddPortia().UseFitzClient(client, fitz => fitz.DisableRequestWorkers()
                .UseFleet(new FleetRunOptions { MembershipSelector = $"lease://{control.Pattern.Realm}/portia-members/*" }).UseOperations(route,
                options => options.QuiescedWorkerCutover = true))
            .AddReactor<JourneyReactor>("journey", WorkloadScope.Global, options => options.PollInterval = TimeSpan.FromMilliseconds(10))
            .AddReactor<IndependentReactor>("independent", WorkloadScope.Global, options => options.PollInterval = TimeSpan.FromMilliseconds(10))
            .AddWorkers();
        return services;
    }

    static async Task<List<DomainEventRecord>> ReadEffectsAsync(FitzEventStore events, string realm)
    {
        var results = new List<DomainEventRecord>();
        await foreach (var record in events.ReadAsync(EventStreamPattern.ForPattern(realm, "accounts"), EventCursor.Start, default))
            results.Add(record);
        return results;
    }

    static async Task WaitAsync(Func<Task<bool>> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await ready())
            await Task.Delay(10, timeout.Token);
    }

    internal sealed class JourneyControl(EventStreamPattern pattern)
    {
        internal EventStreamPattern Pattern { get; } = pattern;
        internal volatile bool Fail = true;
        internal int Invocations;
        internal Exception? LastException;
        internal TaskCompletionSource IndependentProgress { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed partial class JourneyReactor(IProjectionCheckpointStore checkpoints, IEventStore events, JourneyControl control)
        : BatchReactor(checkpoints, control.Pattern), IReactorHandler<ValueChanged>
    {
        public async ValueTask<Result> HandleAsync(IReactorContext<ValueChanged> context, CancellationToken ct)
        {
            Interlocked.Increment(ref control.Invocations);
            if (context.Trigger.Value == 200 && control.Fail)
                return Result.Failure(RequestError.Validation("injected participant outage"));
            try
            {
                var effect = CreateEffectId(context, "account");
                var stream = new EventStreamAddress(context.Source.Stream.Realm, "accounts", effect.ToString());
                await foreach (var existing in events.ReadAsync(stream, 0, ct))
                {
                    Assert.Equal(context.Trigger.Value, Assert.IsType<ValueChanged>(existing.Event).Value);
                    return Result.Success;
                }
                var change = new ValueChanged(context.Trigger.Value);
                change.AttachMetadata(new DomainEventMetadata(effect, effect, 1, context.StartedAt,
                    context.CorrelationId, context.CauseId)
                { ExecutionId = context.ExecutionId, Actor = ActorAttribution.FromPrincipal(context.Actor) });
                await events.AppendAsync(stream, 0, [change], ct);
                return Result.Success;
            }
            catch (Exception ex) { control.LastException = ex; throw; }
        }
    }

    internal sealed partial class IndependentReactor(IProjectionCheckpointStore checkpoints, JourneyControl control)
        : Reactor(checkpoints, control.Pattern), IReactorHandler<ValueChanged>
    {
        public ValueTask<Result> HandleAsync(IReactorContext<ValueChanged> context, CancellationToken ct)
        {
            if (context.Trigger.Value == 200)
                control.IndependentProgress.TrySetResult();
            return ValueTask.FromResult(Result.Success);
        }
    }
}

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class TypedReactorHostingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRetryWithCausationAndCheckpointGivenPlainHostedReactor(bool perTenant)
    {
        // Arrange
        const string name = "accounts.record-deposit";
        var tenant = new TenantId("plain-reactor-tenant");
        var realm = perTenant ? tenant.Value : ConsumerStreams.Realm;
        var source = perTenant ? EventStreamPattern.ForTenant("plain") : EventStreamPattern.ForPattern(realm, "plain");
        var checkpoints = new InMemoryProjectionCheckpointStore();
        var attempts = new Attempts();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<IProjectionCheckpointStore>(checkpoints);
        _ = services.AddSingleton(attempts);
        _ = services.AddSingleton<ITenantDirectory>(new FixedTenantDirectory(tenant));
        _ = services.AddPortia().AddReactor<DepositHandler, Deposited>(name, source,
            options => options.PollInterval = TimeSpan.FromMilliseconds(10)).AddWorkers().UseSingleProcessWorkloads();
        await using var provider = ConsumerHost.Build(services);
        var store = provider.GetRequiredService<IEventStore>();
        var aggregateId = Uuid.CreateVersion4();
        var trigger = DomainEventSeed.Attach(new Deposited(7), aggregateId, 1);
        var stream = new EventStreamAddress(realm, "plain", aggregateId.ToString());
        await store.AppendAsync(stream, 0, [trigger], TestContext.Current.CancellationToken);
        var identity = new CheckpointIdentity(name, EventStreamPattern.ForPattern(realm, "plain"));
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await worker.StartAsync(timeout.Token);
        var first = await attempts.Observed.Reader.ReadAsync(timeout.Token);
        var retry = await attempts.Observed.Reader.ReadAsync(timeout.Token);
        var beforeCommit = await checkpoints.LoadAsync(identity, timeout.Token);
        attempts.Complete.TrySetResult();
        ProjectionCheckpoint committed;
        do
        {
            await Task.Delay(10, timeout.Token);
            committed = await checkpoints.LoadAsync(identity, timeout.Token);
        } while (committed == ProjectionCheckpoint.Start);
        await worker.StopAsync(timeout.Token);

        // Assert
        Assert.Equal(ProjectionCheckpoint.Start, beforeCommit);
        Assert.Equal(new ProjectionCheckpoint(retry.Context.Source.NextCursor), committed);
        Assert.Equal(trigger.Metadata.EventId, first.Context.CauseId);
        Assert.Equal(first.Context.CauseId, retry.Context.CauseId);
        Assert.Equal(first.Context.CorrelationId, retry.Context.CorrelationId);
        Assert.Equal(stream, retry.Context.Source.Stream);
        Assert.Equal(7, retry.Context.Trigger.Amount);
        Assert.True(RequestActor.IsSystem(retry.Context.Actor));
        Assert.NotEqual(first.Context.ExecutionId, retry.Context.ExecutionId);
        Assert.NotEqual(first.ScopeId, retry.ScopeId);
    }

    [Fact]
    public void ShouldRejectAChangedSourceGivenTheSamePlainReactorRegistration()
    {
        // Arrange
        var services = new ServiceCollection();
        var portia = services.AddPortia();
        _ = portia.AddReactor<DepositHandler, Deposited>("record", EventStreamPattern.ForTenant("accounts"));
        _ = portia.AddReactor<DepositHandler, Deposited>("record", EventStreamPattern.ForTenant("accounts"));
        _ = portia.AddFitz(new Cntryl.Fitz.ClientConfig(new Uri("ws://127.0.0.1:1/ws")),
            fitz => fitz.UseOperations("kv://test/state/progress"));
        using var provider = services.BuildServiceProvider();

        // Act
        var failure = Record.Exception(() => portia.AddReactor<DepositHandler, Deposited>("record",
            EventStreamPattern.ForTenant("other")));
        var topology = provider.GetRequiredService<IApplicationTopology>().Describe();

        // Assert
        Assert.Contains("record", Assert.IsType<InvalidOperationException>(failure).Message, StringComparison.Ordinal);
        var processor = Assert.Single(topology.Processors);
        Assert.Equal("record", processor.Name);
        Assert.Equal(typeof(DepositHandler).FullName, processor.Component);
        Assert.Contains(typeof(Deposited).FullName!, Assert.Single(processor.Subscriptions), StringComparison.Ordinal);
    }

    sealed class DepositHandler(Attempts attempts, IConsumerScope scope) : IReactorHandler<Deposited>
    {
        public async ValueTask<Result> HandleAsync(IReactorContext<Deposited> context, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref attempts.Count);
            await attempts.Observed.Writer.WriteAsync(new Invocation(context, scope.Id), ct);

            if (attempt == 1)
                return Result.Failure(RequestError.Conflict("effect still pending", isTransient: true));


            await attempts.Complete.Task.WaitAsync(ct);
            return Result.Success;
        }
    }

    sealed class Attempts
    {
        public readonly Channel<Invocation> Observed = Channel.CreateUnbounded<Invocation>();
        public readonly TaskCompletionSource Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count;
    }

    sealed record Invocation(IReactorContext<Deposited> Context, Guid ScopeId);
}

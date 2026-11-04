using System.Collections.Concurrent;
using System.Security.Claims;
using Cntryl.Fitz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Exercises the public one-call client against endpoints hosted only by owned workloads.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class ProcessorStatusRpcIntegrationTests(FitzBrokerFixture broker)
{
    /// <summary>Projectors and reactors report native progress through RPC, then disappear when ownership stops.</summary>
    /// <param name="operationalPolicy">Whether the application adds an optional operational scope policy.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReportOwnedProjectorAndReactorProgressThroughPublicRpcClient(bool operationalPolicy)
    {
        await using var connection = await broker.CreateClientAsync();
        var application = "status-" + Uuid.CreateVersion4();
        var control = new StatusControl();
        var events = new InMemoryEventStore();
        var aggregate = Uuid.CreateVersion4();
        var source = new ValueChanged(1);
        source.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregate, 1, DateTimeOffset.UtcNow));
        await events.AppendAsync(new EventStreamAddress("status-source", "events", aggregate.ToString()), 0, [source]);
        var services = new ServiceCollection();
        _ = services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["PORTIA_SYSTEM_ADMIN_ROLE"] = "platform-operators" }).Build());
        _ = services.AddSingleton<IEventStore>(events);
        _ = services.AddSingleton(control);
        _ = services.AddSingleton<StatusStore>();
        _ = services.AddSingleton<IRequestActorValidator, StatusActorValidator>();
        if (operationalPolicy)
            _ = services.AddSingleton<IOperationalAuthorizer, TestOperationalAuthorizer>();
        _ = services.AddPortia().UseSingleProcessWorkloads()
            .UseFitzClient(connection, fitz => fitz.DisableRequestWorkers().UseProcessorStatusRpc(application))
            .AddProjector<StatusProjector>(StatusControl.ProjectorName, WorkloadScope.Global,
                options => options.PollInterval = TimeSpan.FromMilliseconds(10))
            .AddReactor<StatusReactor>(StatusControl.ReactorName, WorkloadScope.Global,
                options => options.PollInterval = TimeSpan.FromMilliseconds(10))
            .AddWorkers();
        await using var provider = services.BuildServiceProvider();
        var callerServices = new ServiceCollection();
        _ = callerServices.AddPortia().UseFitzClient(connection,
            fitz => fitz.DisableRequestWorkers().UseProcessorStatusRpc(application));
        await using var caller = callerServices.BuildServiceProvider();
        Assert.DoesNotContain(caller.GetServices<IHostedService>(), service => service is PortiaWorkloadService);
        var client = caller.GetRequiredService<IProcessorStatusClient>();
        var worker = provider.GetServices<IHostedService>().OfType<PortiaWorkloadService>().Single();
        await worker.StartAsync(default);
        try
        {
            await Task.WhenAll(control.ProjectorEntered.Task, control.ReactorEntered.Task)
                .WaitAsync(TimeSpan.FromSeconds(15));
            foreach (var name in new[] { StatusControl.ProjectorName, StatusControl.ReactorName })
            {
                var pending = await client.GetAsync(name, actorToken: "global");
                Assert.True(pending.IsSuccess);
                Assert.Equal(name, pending.Value.Processor);
                Assert.Equal(EventCursor.Start, pending.Value.Progress.Position);
                Assert.Equal(1UL, pending.Value.Progress.PendingEvents);
                Assert.False(pending.Value.Progress.IsCaughtUp);
            }
            var denied = await client.GetAsync(StatusControl.ProjectorName);
            Assert.False(denied.IsSuccess);
            Assert.Equal(RequestErrorKind.Unauthorized, denied.Error.Kind);
            var outsideScope = await client.GetAsync(StatusControl.ReactorName, actorToken: "tenant-a");
            Assert.Equal(!operationalPolicy, outsideScope.IsSuccess);
            if (operationalPolicy)
            {
                Assert.False(outsideScope.IsSuccess);
                Assert.Equal(RequestErrorKind.Forbidden, outsideScope.Error.Kind);
            }
            var member = await client.GetAsync(StatusControl.ProjectorName, actorToken: "member");
            Assert.False(member.IsSuccess);
            Assert.Equal(RequestErrorKind.Forbidden, member.Error.Kind);
            control.Release.TrySetResult();
            foreach (var name in new[] { StatusControl.ProjectorName, StatusControl.ReactorName })
            {
                var complete = await WaitForCaughtUpAsync(client, name);
                Assert.NotEqual(EventCursor.Start, complete.Progress.Position);
                Assert.Equal(0UL, complete.Progress.PendingEvents);
                Assert.Equal(name == StatusControl.ProjectorName ? "projector" : "reactor", complete.Kind);
            }
        }
        finally
        {
            control.Release.TrySetResult();
            await worker.StopAsync(default);
            worker.Dispose();
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.GetAsync(StatusControl.ProjectorName, actorToken: "global", ct: deadline.Token));
    }

    static async Task<ProcessorStatus> WaitForCaughtUpAsync(IProcessorStatusClient client, string processor)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var response = await client.GetAsync(processor, actorToken: "global", ct: deadline.Token);
            Assert.True(response.IsSuccess);
            if (response.Value.Progress.IsCaughtUp == true)
                return response.Value;
            await Task.Delay(10, deadline.Token);
        }
    }

    sealed class StatusControl
    {
        internal const string ProjectorName = "balances / \u03b1";
        internal const string ReactorName = "receipt delivery";
        internal TaskCompletionSource ProjectorEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReactorEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class StatusProjector(StatusStore store, StatusControl control)
        : Projector(store, EventStreamPattern.ForPattern("status-source", "events"))
    {
        protected override async ValueTask<Result> ProjectEventAsync(DomainEventRecord record,
            IProjectorContext context, CancellationToken ct)
        {
            control.ProjectorEntered.TrySetResult();
            await control.Release.Task.WaitAsync(ct);
            return Result.Success;
        }
    }

    sealed class StatusReactor(StatusStore store, StatusControl control)
        : Reactor(store, EventStreamPattern.ForPattern("status-source", "events"))
    {
        protected override async ValueTask<Result> ReactToEventAsync(DomainEventRecord record,
            IExecutionContext context, CancellationToken ct)
        {
            control.ReactorEntered.TrySetResult();
            await control.Release.Task.WaitAsync(ct);
            return Result.Success;
        }
    }

    sealed class StatusStore : IProjectionStore, IProjectionCheckpointStore
    {
        readonly ConcurrentDictionary<CheckpointIdentity, ProjectionCheckpoint> _checkpoints = new();

        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default)
            => ValueTask.FromResult(_checkpoints.GetValueOrDefault(identity));

        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
            => LoadCheckpointAsync(identity, ct);

        public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            _checkpoints[identity] = checkpoint;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
            => ValueTask.FromResult<IProjectionBatch>(new StatusBatch(this, context.Identity));

        sealed class StatusBatch(StatusStore store, CheckpointIdentity identity) : IProjectionBatch
        {
            public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
                => store.SaveAsync(identity, checkpoint, ct);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    sealed class StatusActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default)
        {
            if (token is null)
                return ValueTask.FromResult(Result<ClaimsPrincipal>.Failure(RequestError.Unauthorized("Missing credential.")));
            var actor = TestOperationalAuthorizer.Actor(scope: token == "member" ? "global" : token);
            if (token != "member")
                ((ClaimsIdentity)actor.Identity!).AddClaim(new Claim(ClaimTypes.Role, "platform-operators"));
            return ValueTask.FromResult(Result<ClaimsPrincipal>.Success(actor));
        }
    }
}

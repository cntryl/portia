using System.Collections.Concurrent;
using System.Security.Claims;
using Cntryl.Fitz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Exercises owned workload status and its independent reader against Fitz.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class ProcessorStatusRpcIntegrationTests(FitzBrokerFixture broker)
{
    /// <summary>Projectors and reactors report native progress after their worker releases ownership.</summary>
    /// <param name="operationalPolicy">Whether the application adds an optional operational scope policy.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReportOwnedProgressGivenProjectorAndReactorThroughPublicRpcClient(bool operationalPolicy)
    {
        // Arrange
        await using var connection = await broker.CreateClientAsync();
        var application = "status-" + Uuid.CreateVersion4();
        var control = new StatusControl();
        var events = new InMemoryEventStore();
        var aggregate = Uuid.CreateVersion4();
        var source = new ValueChanged(1);
        source.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregate, 1, DateTimeOffset.UtcNow));
        await events.AppendAsync(new EventStreamAddress("status-source", "events", aggregate.ToString()), 0, [source], TestContext.Current.CancellationToken);
        var store = new StatusStore();
        var fleet = new FleetRunOptions { MembershipSelector = $"lease://{application}/portia-members/*" };
        await using var provider = BuildHost(reader: false);
        await using var readerHost = BuildHost(reader: true);
        var callerServices = new ServiceCollection();
        _ = callerServices.AddPortia().UseFitzClient(connection,
            fitz => fitz.DisableRequestWorkers().UseProcessorStatusRpc(application));
        await using var caller = callerServices.BuildServiceProvider();
        var client = caller.GetRequiredService<IProcessorStatusClient>();
        var worker = provider.GetServices<IHostedService>().OfType<PortiaWorkloadService>().Single();
        var reader = readerHost.GetServices<IHostedService>().OfType<FitzProcessorStatusReader>().Single();
        await reader.StartAsync(TestContext.Current.CancellationToken);
        await worker.StartAsync(TestContext.Current.CancellationToken);

        var pending = new Dictionary<string, Result<ProcessorStatus>>(StringComparer.Ordinal);
        var completed = new Dictionary<string, ProcessorStatus>(StringComparer.Ordinal);
        var names = new[] { StatusControl.ProjectorName, StatusControl.ReactorName };
        var ct = TestContext.Current.CancellationToken;
        Result<ProcessorStatus> denied;
        Result<ProcessorStatus> outsideScope;
        Result<ProcessorStatus> member;

        // Act
        try
        {
            await Task.WhenAll(control.ProjectorEntered.Task, control.ReactorEntered.Task)
                .WaitAsync(TimeSpan.FromSeconds(15), ct);

            foreach (var name in names)
                pending.Add(name, await GetStatusWhenReadyAsync(client, name, "global", ct));


            denied = await GetStatusWhenReadyAsync(client, StatusControl.ProjectorName, ct: ct);
            outsideScope = await GetStatusWhenReadyAsync(client, StatusControl.ReactorName, "tenant-a", ct);
            member = await GetStatusWhenReadyAsync(client, StatusControl.ProjectorName, "member", ct);
            _ = control.Release.TrySetResult();

            foreach (var name in names)
                completed.Add(name, await WaitForCaughtUpAsync(client, name));
        }
        finally
        {
            _ = control.Release.TrySetResult();
            await worker.StopAsync(ct);
            worker.Dispose();
        }

        var afterStop = await GetStatusWhenReadyAsync(client, StatusControl.ProjectorName, "global", ct);
        var deniedAfterStop = await GetStatusWhenReadyAsync(client, StatusControl.ProjectorName, "member", ct);
        await reader.StopAsync(ct);

        // Assert
        Assert.DoesNotContain(readerHost.GetServices<IHostedService>(), service => service is PortiaWorkloadService);

        foreach (var name in names)
        {
            var status = pending[name];
            Assert.True(status.IsSuccess);
            Assert.Equal(name, status.Value.Processor);
            Assert.True(status.Value.Owner?.HasOwner);
            Assert.Equal("processing", status.Value.Execution?.Phase);
            var progress = Assert.IsType<EventStreamProgress>(status.Value.Progress);
            Assert.Equal(EventCursor.Start, progress.Position);
            Assert.Equal(1UL, progress.PendingEvents);
            Assert.False(progress.IsCaughtUp);

            var complete = completed[name];
            var finalProgress = Assert.IsType<EventStreamProgress>(complete.Progress);
            Assert.NotEqual(EventCursor.Start, finalProgress.Position);
            Assert.Equal(0UL, finalProgress.PendingEvents);
            Assert.Equal(name == StatusControl.ProjectorName ? "projector" : "reactor", complete.Kind);
        }


        Assert.False(denied.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, denied.Error.Kind);
        Assert.Equal(!operationalPolicy, outsideScope.IsSuccess);

        if (operationalPolicy)
            Assert.Equal(RequestErrorKind.Forbidden, Assert.IsType<RequestError>(outsideScope.Error).Kind);

        Assert.False(member.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, member.Error.Kind);
        Assert.True(afterStop.IsSuccess);
        Assert.NotEqual(EventCursor.Start, afterStop.Value.Progress!.Position);
        Assert.False(afterStop.Value.Owner?.HasOwner);
        Assert.Equal("owner_unavailable", afterStop.Value.Execution?.Phase);
        Assert.False(deniedAfterStop.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, deniedAfterStop.Error.Kind);

        ServiceProvider BuildHost(bool reader)
        {
            var services = new ServiceCollection();
            _ = services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["PORTIA_SYSTEM_ADMIN_ROLE"] = "platform-operators" }).Build());
            _ = services.AddSingleton<IEventStore>(events);
            _ = services.AddSingleton(control);
            _ = services.AddSingleton(store);
            _ = services.AddSingleton<IRequestActorValidator, StatusActorValidator>();

            if (operationalPolicy)
                _ = services.AddSingleton<IOperationalAuthorizer, TestOperationalAuthorizer>();

            var applicationBuilder = services.AddPortia()
                .UseFitzClient(connection, fitz =>
                {
                    _ = fitz.DisableRequestWorkers().UseFleet(fleet).UseProcessorStatusRpc(application);

                    if (reader)
                        _ = fitz.AddProcessorStatusReader();
                })
                .AddProjector<StatusProjector>(StatusControl.ProjectorName, WorkloadScope.Global,
                    options => options.PollInterval = TimeSpan.FromMilliseconds(10))
                .AddReactor<StatusReactor>(StatusControl.ReactorName, WorkloadScope.Global,
                    options => options.PollInterval = TimeSpan.FromMilliseconds(10));

            if (!reader)
                _ = applicationBuilder.AddWorkers();

            return services.BuildServiceProvider();
        }
    }

    /// <summary>Preserves partial status when an owned endpoint times out probing source progress.</summary>
    [Fact]
    public async Task ShouldReturnOwnedPartialStatusGivenSourceProbeTimeout()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await broker.CreateClientAsync();
        var application = "status-" + Uuid.CreateVersion4();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["PORTIA_SYSTEM_ADMIN_ROLE"] = "platform-operators" }).Build());
        _ = services.AddSingleton<StatusStore>();
        _ = services.AddSingleton<StatusControl>();
        _ = services.AddSingleton<IRequestActorValidator, StatusActorValidator>();
        _ = services.AddSingleton<IDomainEventProgressReader, WaitingStatusProbe>();
        _ = services.AddPortia()
            .UseFitzClient(connection, fitz => fitz.DisableRequestWorkers().UseProcessorStatusRpc(application))
            .AddProjector<StatusProjector>(StatusControl.ProjectorName, WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();
        await using var endpoint = await provider.GetRequiredService<IProcessorStatusRpcRegistrar>()
            .RegisterAsync(new WorkloadIdentity(StatusControl.ProjectorName), ct);
        var client = provider.GetRequiredService<IProcessorStatusClient>();
        Result<ProcessorStatus> status = default;

        // Act
        var error = await Record.ExceptionAsync(async () =>
            status = await client.GetAsync(StatusControl.ProjectorName, actorToken: "global", ct: ct));

        // Assert
        Assert.Null(error);
        Assert.True(status.IsSuccess);
        Assert.Equal("Timeout", status.Value.SourceError);
        Assert.NotNull(status.Value.Progress);
    }

    sealed class WaitingStatusProbe : IDomainEventProgressReader
    {
        public async ValueTask<EventStreamProgress> GetProgressAsync(
            EventStreamPattern pattern,
            EventCursor position,
            CancellationToken ct = default
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The status probe should reach its timeout.");
        }
    }

    static async Task<ProcessorStatus> WaitForCaughtUpAsync(IProcessorStatusClient client, string processor)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        while (true)
        {
            var response = await GetStatusWhenReadyAsync(client, processor, "global", deadline.Token);
            Assert.True(response.IsSuccess);

            if (response.Value.Progress!.IsCaughtUp == true)
                return response.Value;

            await Task.Delay(10, deadline.Token);
        }
    }

    static async Task<Result<ProcessorStatus>> GetStatusWhenReadyAsync(
        IProcessorStatusClient client,
        string processor,
        string? actorToken = null,
        CancellationToken ct = default
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));

        while (true)
        {
            try
            {
                return await client.GetAsync(processor, actorToken: actorToken, ct: deadline.Token);
            }
            catch (RpcException ex) when (ex is
            {
                DomainCode: FitzErrorCodes.RpcBackpressure, Status: 1, ErrorClass: 5,
                ErrorEffect: 0
            })
            {
                // A terminal response can precede worker scope cleanup. Only a native, not-applied
                // backpressure response is retried; authorization results and all other failures escape.
                await Task.Delay(10, deadline.Token);
            }
        }
    }

    sealed class StatusControl
    {
        internal const string ProjectorName = "balances / \u03b1";
        internal const string ReactorName = "receipt delivery";

        internal TaskCompletionSource ProjectorEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReactorEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class StatusProjector(StatusStore store, StatusControl control)
        : Projector(store, EventStreamPattern.ForPattern("status-source", "events"))
    {
        protected override async ValueTask<Result> ProjectEventAsync(
            DomainEventRecord record,
            IProjectorContext context,
            CancellationToken ct
        )
        {
            control.ProjectorEntered.TrySetResult();
            await control.Release.Task.WaitAsync(ct);
            return Result.Success;
        }
    }

    sealed class StatusReactor(StatusStore store, StatusControl control)
        : Reactor(store, EventStreamPattern.ForPattern("status-source", "events"))
    {
        protected override async ValueTask<Result> ReactToEventAsync(
            DomainEventRecord record,
            IExecutionContext context,
            CancellationToken ct
        )
        {
            control.ReactorEntered.TrySetResult();
            await control.Release.Task.WaitAsync(ct);
            return Result.Success;
        }
    }

    sealed class StatusStore : IProjectionStore, IProjectionCheckpointStore, IConditionalProjectionCheckpointStore
    {
        readonly ConcurrentDictionary<CheckpointIdentity, ProjectionCheckpoint> _checkpoints = new();

        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(
            CheckpointIdentity identity,
            CancellationToken ct = default
        )
            => ValueTask.FromResult(_checkpoints.GetValueOrDefault(identity));

        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
            => LoadCheckpointAsync(identity, ct);

        public ValueTask SaveAsync(
            CheckpointIdentity identity,
            ProjectionCheckpoint checkpoint,
            CancellationToken ct = default
        )
        {
            _checkpoints[identity] = checkpoint;
            return ValueTask.CompletedTask;
        }

        ValueTask IConditionalProjectionCheckpointStore.SaveAsync(
            CheckpointIdentity identity,
            ProjectionCheckpoint expected,
            ProjectionCheckpoint checkpoint,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();

            if (_checkpoints.GetValueOrDefault(identity) != expected)
                throw new ProjectionConcurrencyException("stale checkpoint");

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
                return ValueTask.FromResult(
                    Result<ClaimsPrincipal>.Failure(RequestError.Unauthorized("Missing credential.")));

            var actor = TestOperationalAuthorizer.Actor(scope: token == "member" ? "global" : token);

            if (token != "member")
                ((ClaimsIdentity)actor.Identity!).AddClaim(new Claim(ClaimTypes.Role, "platform-operators"));

            return ValueTask.FromResult(Result<ClaimsPrincipal>.Success(actor));
        }
    }
}

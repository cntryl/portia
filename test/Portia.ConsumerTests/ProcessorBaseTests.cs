using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class ProcessorBaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InjectedRepositoryCommitsDataAndProgressAtTheSelectedBoundary(bool batch)
    {
        var events = await Seed();
        var repository = new Repository();
        Projector projector = batch ? new BatchAccounts(repository) : new Accounts(repository);
        var runner = new ProjectorRunner(events);
        _ = await runner.RunAsync(projector, ProjectionCheckpoint.Start, new ProjectionRunOptions { MaxBatchSize = 2 });
        Assert.Equal(6, repository.Value);
        Assert.Equal(batch ? new ulong[] { 2, 3 } : [1, 2, 3], repository.Commits);
        Assert.Equal(batch ? 2 : 3, repository.Disposals);
        Assert.Equal(3UL, repository.Checkpoint.NextOffset);
    }

    [Fact]
    public async Task FailedBatchRollsBackAndResumesFromTheRepositoryCheckpoint()
    {
        var events = await Seed();
        var repository = new Repository { Fail = true };
        var runner = new ProjectorRunner(events);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new BatchAccounts(repository), ProjectionCheckpoint.Start).AsTask());
        Assert.Equal(0, repository.Value);
        Assert.Equal(ProjectionCheckpoint.Start, repository.Checkpoint);
        Assert.Equal(1, repository.Disposals);
        repository.Fail = false;
        _ = await runner.RunAsync(new BatchAccounts(repository), repository.Checkpoint);
        Assert.Equal(6, repository.Value);
        Assert.Equal(3UL, repository.Checkpoint.NextOffset);
    }

    [Fact]
    public void RegistrationNeedsOnlyTheApplicationRepository()
    {
        var services = new ServiceCollection();
        _ = services.AddScoped<Repository>();
        _ = services.AddSingleton<IDomainEventReader, InMemoryEventStore>();
        _ = services.AddPortia().AddProjector<BatchAccounts>(WorkloadScope.PerTenant);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<BatchAccounts>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReactionsOwnTheirCheckpointsAndKeepPerEventCausality(bool batch)
    {
        var events = await Seed();
        var repository = new ReactionRepository();
        Reactor reactor = batch ? new BatchReaction(repository) : new Reaction(repository);
        _ = await new ReactorRunner(events).RunAsync(reactor, ProjectionCheckpoint.Start, 2);
        Assert.Equal(batch ? new ulong[] { 2, 3 } : [1, 2, 3], repository.Commits);
        Assert.Equal(3, repository.Contexts.Select(c => c.ExecutionId).Distinct().Count());
        Assert.All(repository.Contexts, context =>
        {
            Assert.True(RequestActor.IsSystem(context.Actor));
            Assert.Equal(context.Source.Event.Metadata.EventId, context.CauseId);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReactionCheckpointReplaysEffectsButNeverAdvancesProgress(bool batch)
    {
        var events = await Seed();
        var repository = new ReactionRepository { FailSave = true };
        Reactor reactor = batch ? new BatchReaction(repository) : new Reaction(repository);
        var runner = new ReactorRunner(events);
        _ = await Assert.ThrowsAsync<IOException>(() =>
            runner.RunAsync(reactor, ProjectionCheckpoint.Start, 2).AsTask());
        Assert.Equal(ProjectionCheckpoint.Start, repository.Checkpoint);
        Assert.Equal(batch ? 2 : 1, repository.Contexts.Count);
        repository.FailSave = false;
        _ = await runner.RunAsync(reactor, repository.Checkpoint, 2);
        Assert.Equal(batch ? 5 : 4, repository.Contexts.Count);
        Assert.Equal(repository.Contexts[0].CauseId, repository.Contexts[batch ? 2 : 1].CauseId);
        Assert.NotEqual(repository.Contexts[0].ExecutionId, repository.Contexts[batch ? 2 : 1].ExecutionId);
    }

    [Fact]
    public async Task CancellationDiscardsBufferedWritesAndDoesNotCommitCheckpoint()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new Repository { OnAdd = cancellation.Cancel };
        var runner = new ProjectorRunner(await Seed());
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new BatchAccounts(repository), ProjectionCheckpoint.Start, ct: cancellation.Token)
                .AsTask());
        Assert.Equal(0, repository.Value);
        Assert.Equal(ProjectionCheckpoint.Start, repository.Checkpoint);
        Assert.Equal(1, repository.Disposals);
    }

    static async Task<InMemoryEventStore> Seed()
    {
        var events = new InMemoryEventStore();
        var id = Uuid.CreateVersion4();
        await events.AppendAsync(new EventStreamAddress("bases", "accounts", id.ToString()), 0,
        [
            DomainEventSeed.Attach(new Deposited(1), id, 1), DomainEventSeed.Attach(new Deposited(2), id, 2),
            DomainEventSeed.Attach(new Deposited(3), id, 3)
        ]);
        return events;
    }

    sealed class Reaction(ReactionRepository repository)
        : Reactor(repository, EventStreamPattern.ForPattern("bases", "accounts"))
    {
        protected override ValueTask ReactToEventAsync(DomainEventRecord record, IExecutionContext context,
            CancellationToken ct)
        {
            repository.Contexts.Add((IReactorContext)context);
            return ValueTask.CompletedTask;
        }
    }

    sealed class BatchReaction(ReactionRepository repository)
        : BatchReactor(repository, EventStreamPattern.ForPattern("bases", "accounts"))
    {
        protected override ValueTask ReactBatchAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct)
        {
            repository.Contexts.AddRange(contexts);
            return ValueTask.CompletedTask;
        }
    }

    sealed class ReactionRepository : IProjectionCheckpointStore
    {
        public ProjectionCheckpoint Checkpoint;
        public bool FailSave;
        public List<ulong> Commits { get; } = [];
        public List<IReactorContext> Contexts { get; } = [];

        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default) =>
            ValueTask.FromResult(Checkpoint);

        public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint,
            CancellationToken ct = default)
        {
            if (FailSave)
            {
                throw new IOException("Checkpoint write failed");
            }

            Checkpoint = checkpoint;
            Commits.Add(checkpoint.NextOffset);
            return ValueTask.CompletedTask;
        }
    }

    sealed class Accounts(Repository repository)
        : Projector(repository, EventStreamPattern.ForPattern("bases", "accounts"))
    {
        protected override ValueTask ProjectEventAsync(DomainEventRecord record, IProjectorContext context,
            CancellationToken ct)
        {
            repository.Add(((Deposited)record.Event).Amount);
            return ValueTask.CompletedTask;
        }
    }

    sealed class BatchAccounts(Repository repository)
        : BatchProjector(repository, EventStreamPattern.ForPattern("bases", "accounts"))
    {
        protected override ValueTask ProjectBatchAsync(IReadOnlyList<DomainEventRecord> records,
            IProjectorContext context, CancellationToken ct)
        {
            foreach (var record in records)
                repository.Add(((Deposited)record.Event).Amount);
            return ValueTask.CompletedTask;
        }
    }

    sealed class Repository : IProjectionStore
    {
        public ProjectionCheckpoint Checkpoint;
        public int Disposals;
        public bool Fail;
        public Action? OnAdd;
        public int Value;
        int _pending;
        public List<ulong> Commits { get; } = [];

        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity,
            CancellationToken ct = default) => ValueTask.FromResult(Checkpoint);

        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
        {
            _pending = 0;
            return ValueTask.FromResult<IProjectionBatch>(new Batch(this));
        }

        public void Add(int value)
        {
            _pending += value;
            OnAdd?.Invoke();
        }

        sealed class Batch(Repository repository) : IProjectionBatch
        {
            public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (repository.Fail)
                {
                    throw new InvalidOperationException("Commit failed");
                }

                repository.Value += repository._pending;
                repository.Checkpoint = checkpoint;
                repository.Commits.Add(checkpoint.NextOffset);
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                repository._pending = 0;
                repository.Disposals++;
                return ValueTask.CompletedTask;
            }
        }
    }
}

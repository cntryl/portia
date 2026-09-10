using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class CheckpointIdentityTests
{
    [Fact]
    public async Task ReactorsResumeIndependentlyAcrossTenantsAndPatterns()
    {
        var store = new InMemoryProjectionCheckpointStore();
        var identities = new[]
        {
            new CheckpointIdentity("same", EventStreamPattern.ForPattern("tenant-a", "orders")),
            new CheckpointIdentity("same", EventStreamPattern.ForPattern("tenant-b", "orders")),
            new CheckpointIdentity("same", EventStreamPattern.ForPattern("tenant-a", "returns")),
        };
        for (var i = 0; i < identities.Length; i++)
            await store.SaveAsync(identities[i], new ProjectionCheckpoint((ulong)i + 1));
        for (var i = 0; i < identities.Length; i++)
            Assert.Equal((ulong)i + 1, (await store.LoadAsync(identities[i])).NextOffset);
        Assert.Equal(identities[0], new CheckpointIdentity("same", EventStreamPattern.ForPattern("tenant-a", "orders", "")));
    }

    [Fact]
    public async Task RebuildGenerationsResumeAndLeaveLiveDataUntouched()
    {
        var events = new InMemoryEventStore();
        var target = new Target();
        var pattern = EventStreamPattern.ForPattern("tenant", "orders");
        var stream = new EventStreamAddress("tenant", "orders", "one");
        var id = Uuid.CreateVersion4();
        await events.AppendAsync(stream, 0, [DomainEventSeed.Attach(new Declined("one"), id, 1)]);
        async Task Pass(string? rebuildId)
        {
            var services = new ServiceCollection();
            _ = services.AddSingleton(new Projection(target, pattern));
            _ = services.AddSingleton(new ProjectorRunner(events));
            await using var provider = services.BuildServiceProvider();
            await ProjectorRegistration.Create<Projection>().RunPass(provider, new ProjectionRunOptions { RebuildId = rebuildId }, default);
        }
        var live = new CheckpointIdentity("same", pattern);
        var first = new CheckpointIdentity("same", pattern, "first");
        var second = new CheckpointIdentity("same", pattern, "second");
        await Pass(null);
        await Pass("first");
        Assert.True(Assert.Single(target.Data[first]));
        await events.AppendAsync(stream, 1, [DomainEventSeed.Attach(new Declined("two"), id, 2)]);
        await Pass("first");
        await Pass("first");
        await Pass("second");
        Assert.Equal(2, target.Data[first].Count);
        Assert.Equal(2, target.Data[second].Count);
        Assert.False(Assert.Single(target.Data[live]));
        Assert.Equal(1UL, (await target.LoadCheckpointAsync(live)).NextOffset);
        Assert.Equal(2UL, (await target.LoadCheckpointAsync(first)).NextOffset);
        Assert.Equal(2UL, (await target.LoadCheckpointAsync(second)).NextOffset);
        Assert.All(target.Loads, identity => Assert.Contains(identity, target.Begins));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("component", "")]
    [InlineData("component", " ")]
    public void IdentityRejectsBlankNamesAndNonNullBlankGenerations(string component, string? rebuild)
        => Assert.Throws<ArgumentException>(() => new CheckpointIdentity(component, EventStreamPattern.ForPattern("tenant"), rebuild));

    [Fact]
    public async Task PublicRunnerReloadsEachPatternAndResumesInterruptedRebuild()
    {
        var events = new InMemoryEventStore();
        var target = new Target();
        var patterns = new[]
        {
            EventStreamPattern.ForPattern("tenant-a", "orders"),
            EventStreamPattern.ForPattern("tenant-b", "orders"),
            EventStreamPattern.ForPattern("tenant-a", "returns"),
        };
        foreach (var pattern in patterns)
        {
            var id = Uuid.CreateVersion4();
            await events.AppendAsync(new EventStreamAddress(pattern.Realm, pattern.Area!, "one"), 0,
                [DomainEventSeed.Attach(new Declined("one"), id, 1), DomainEventSeed.Attach(new Declined("two"), id, 2)]);
        }
        async Task Pass(EventStreamPattern pattern)
        {
            var services = new ServiceCollection();
            _ = services.AddSingleton(new Projection(target, pattern));
            _ = services.AddSingleton(new ProjectorRunner(events));
            await using var provider = services.BuildServiceProvider();
            await ProjectorRegistration.Create<Projection>().RunPass(provider,
                new ProjectionRunOptions { RebuildId = "interrupted", MaxBatchSize = 1 }, default);
        }
        target.FailCommitNumber = 2;
        _ = await Assert.ThrowsAsync<IOException>(() => Pass(patterns[0]));
        var first = new CheckpointIdentity("same", patterns[0], "interrupted");
        Assert.Equal(1UL, (await target.LoadCheckpointAsync(first)).NextOffset);
        foreach (var pattern in patterns)
        {
            await Pass(pattern);
            await Pass(pattern);
            var identity = new CheckpointIdentity("same", pattern, "interrupted");
            Assert.Equal(2, target.Data[identity].Count);
            Assert.Equal(2UL, (await target.LoadCheckpointAsync(identity)).NextOffset);
        }
    }

    sealed class Projection(Target target, EventStreamPattern pattern) : BatchProjector(target, pattern, "same")
    {
        protected override ValueTask ProjectEventAsync(DomainEventRecord record, IProjectorContext context, CancellationToken ct)
        {
            target.Add(context.IsRebuild);
            return ValueTask.CompletedTask;
        }
    }

    sealed class Target : IProjectionStore
    {
        List<bool> _pending = [];
        public void Add(bool value) => _pending.Add(value);
        public int FailCommitNumber { get; set; }
        int _commits;
        public Dictionary<CheckpointIdentity, List<bool>> Data { get; } = [];
        readonly Dictionary<CheckpointIdentity, ProjectionCheckpoint> _checkpoints = [];
        public List<CheckpointIdentity> Loads { get; } = [];
        public List<CheckpointIdentity> Begins { get; } = [];
        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default)
        {
            Loads.Add(identity);
            return ValueTask.FromResult(_checkpoints.GetValueOrDefault(identity));
        }
        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
        {
            Begins.Add(context.Identity);
            _pending = [];
            return ValueTask.FromResult<IProjectionBatch>(new Batch(this, context.Identity));
        }
        sealed class Batch(Target target, CheckpointIdentity identity) : IProjectionBatch
        {

            public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
            {
                if (++target._commits == target.FailCommitNumber)
                    throw new IOException("Interrupted before atomic commit");
                if (!target.Data.TryGetValue(identity, out var data))
                    target.Data[identity] = data = [];
                data.AddRange(target._pending);
                target._checkpoints[identity] = checkpoint;
                return ValueTask.CompletedTask;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

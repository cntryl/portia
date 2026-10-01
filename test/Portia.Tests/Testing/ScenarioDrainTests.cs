namespace Cntryl.Portia;

/// <summary>Finite scenario histories retain bounded production passes and committed progress.</summary>
public sealed class ScenarioDrainTests
{
    /// <summary>Batch bases drain mixed matching/nonmatching records and keep repeat-run semantics.</summary>
    [Theory]
    [InlineData(4097, false)]
    [InlineData(8193, false)]
    [InlineData(4097, true)]
    [InlineData(8193, true)]
    public async Task ShouldDrainBatchAndMixedHistories(int count, bool reaction)
    {
        var events = Enumerable.Range(0, count).Select(index => index % 2 == 0
            ? (DomainEvent)new UserCreated(Uuid.CreateVersion4()) : new ValueChanged(index)).ToArray();
        var expected = events.OfType<UserCreated>().Select(ev => ev.UserId).ToArray();
        var seen = new List<Uuid>();
        if (reaction)
        {
            var scenario = new ReactorScenario().Given(events);
            var reactor = new DrainReactor(new InMemoryProjectionCheckpointStore(), seen);
            await scenario.RunAsync(reactor);
            Assert.Equal(expected, seen);
            await scenario.RunAsync(reactor);
            Assert.Equal(expected.Concat(expected), seen);
        }
        else
        {
            var scenario = new ProjectorScenario().Given(events);
            var projector = new DrainProjector(scenario.Store, seen);
            await scenario.RunAsync(projector);
            Assert.Equal(expected, seen);
            await scenario.RunAsync(projector);
            Assert.Equal(expected, seen);
            var extra = Uuid.CreateVersion4();
            await scenario.Given(new UserCreated(extra)).RunAsync(projector);
            Assert.Equal(expected.Append(extra), seen);
        }
    }

    /// <summary>A second-pass handler failure preserves the checkpoint of the first successful pass.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPropagateSuffixFailureAndKeepCommittedProgress(bool reaction)
    {
        var events = Enumerable.Range(0, 4097).Select(_ => (DomainEvent)new UserCreated(Uuid.CreateVersion4())).ToArray();
        var failure = new InvalidOperationException("suffix failed");
        var seen = new List<Uuid>();
        if (reaction)
        {
            var scenario = new ReactorScenario().Given(events);
            var checkpoints = new InMemoryProjectionCheckpointStore();
            var reactor = new DrainReactor(checkpoints, seen, failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.RunAsync(reactor)));
            var checkpoint = await checkpoints.LoadAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern));
            Assert.Equal("4096", checkpoint.Cursor.ToString());
        }
        else
        {
            var scenario = new ProjectorScenario().Given(events);
            var projector = new DrainProjector(scenario.Store, seen, failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.RunAsync(projector)));
            var checkpoint = await scenario.Store.LoadCheckpointAsync(new CheckpointIdentity(projector.Name, projector.Pattern));
            Assert.Equal("4096", checkpoint.Cursor.ToString());
        }
        Assert.Equal(events.Take(4096).OfType<UserCreated>().Select(ev => ev.UserId), seen);
    }

    /// <summary>Cancellation after a committed pass stops before the next pass and remains resumable.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldObserveCancellationBetweenPasses(bool reaction)
    {
        var ids = Enumerable.Range(0, 4097).Select(_ => Uuid.CreateVersion4()).ToArray();
        using var cancellation = new CancellationTokenSource();
        var seen = new List<Uuid>();
        var events = ids.Select(id => (DomainEvent)new UserCreated(id)).ToArray();
        if (reaction)
        {
            var scenario = new ReactorScenario().Given(events);
            var store = new CancelAfterPassCheckpointStore(new InMemoryProjectionCheckpointStore(), cancellation);
            var reactor = new DrainReactor(store, seen);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.RunAsync(reactor, cancellation.Token));
            Assert.Equal(ids.Take(4096), seen);
            await scenario.RunAsync(reactor);
            Assert.Equal(ids, seen.Skip(4096));
        }
        else
        {
            var scenario = new ProjectorScenario().Given(events);
            var store = new CancelAfterPassStore(scenario.Store, cancellation);
            var projector = new DrainProjector(store, seen);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.RunAsync(projector, cancellation.Token));
            Assert.Equal(ids.Take(4096), seen);
            await scenario.RunAsync(projector);
            Assert.Equal(ids, seen);
        }
    }

    sealed class CancelAfterPassCheckpointStore(IProjectionCheckpointStore inner, CancellationTokenSource cancellation)
        : IProjectionCheckpointStore
    {
        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
            => inner.LoadAsync(identity, ct);
        public async ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            await inner.SaveAsync(identity, checkpoint, ct);
            if (checkpoint.Cursor.ToString() == "4096")
                cancellation.Cancel();
        }
    }

    /// <summary>Caller cancellation inside a batch propagates with no committed progress.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldObserveCancellationWithinPass(bool reaction)
    {
        var events = Enumerable.Range(0, 600).Select(_ => (DomainEvent)new UserCreated(Uuid.CreateVersion4())).ToArray();
        using var cancellation = new CancellationTokenSource();
        var seen = new List<Uuid>();
        if (reaction)
        {
            var scenario = new ReactorScenario().Given(events);
            var store = new InMemoryProjectionCheckpointStore();
            var reactor = new DrainReactor(store, seen, cancellation: cancellation);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.RunAsync(reactor, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(ProjectionCheckpoint.Start, await store.LoadAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern)));
        }
        else
        {
            var scenario = new ProjectorScenario().Given(events);
            var projector = new DrainProjector(scenario.Store, seen, cancellation: cancellation);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.RunAsync(projector, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(ProjectionCheckpoint.Start, await scenario.Store.LoadCheckpointAsync(new CheckpointIdentity(projector.Name, projector.Pattern)));
        }
        Assert.Equal(300, seen.Count);
    }

    sealed class DrainProjector(IProjectionStore store, List<Uuid> seen, Exception? failure = null, CancellationTokenSource? cancellation = null)
        : BatchProjector(store, EventStreamPattern.ForPattern("test", "users"))
    {
        protected override ValueTask ProjectBatchAsync(IReadOnlyList<DomainEventRecord> records,
            IProjectorContext context, CancellationToken ct)
        {
            foreach (var record in records)
            {
                ct.ThrowIfCancellationRequested();
                if (seen.Count == 4096 && failure is not null)
                    throw failure;
                if (record.Event is UserCreated ev)
                    seen.Add(ev.UserId);
                if (seen.Count == 300)
                    cancellation?.Cancel();
            }
            return ValueTask.CompletedTask;
        }
    }

    sealed class DrainReactor(IProjectionCheckpointStore store, List<Uuid> seen, Exception? failure = null, CancellationTokenSource? cancellation = null)
        : BatchReactor(store, EventStreamPattern.ForPattern("test", "users"))
    {
        protected override ValueTask ReactBatchAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct)
        {
            foreach (var context in contexts)
            {
                ct.ThrowIfCancellationRequested();
                if (seen.Count == 4096 && failure is not null)
                    throw failure;
                if (context.Source.Event is UserCreated ev)
                    seen.Add(ev.UserId);
                if (seen.Count == 300)
                    cancellation?.Cancel();
            }
            return ValueTask.CompletedTask;
        }
    }

    sealed class CancelAfterPassStore(IProjectionStore inner, CancellationTokenSource cancellation) : IProjectionStore
    {
        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default)
            => inner.LoadCheckpointAsync(identity, ct);
        public async ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
            => new Batch(await inner.BeginAsync(context, ct), cancellation);
        sealed class Batch(IProjectionBatch innerBatch, CancellationTokenSource cancellation) : IProjectionBatch
        {
            public async ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
            {
                await innerBatch.CommitAsync(checkpoint, ct);
                if (checkpoint.Cursor.ToString() == "4096")
                    cancellation.Cancel();
            }
            public ValueTask DisposeAsync() => innerBatch.DisposeAsync();
        }
    }
}

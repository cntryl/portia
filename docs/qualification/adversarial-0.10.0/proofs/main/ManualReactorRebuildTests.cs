using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>Manual execution rejects projection generations before any reactor work begins.</summary>
public sealed class ManualReactorRebuildTests
{
    /// <summary>Neither reactor shape can silently run a projection rebuild against live effects and progress.</summary>
    /// <param name="batch">Whether the manually driven component handles a batch.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectRebuildGenerationBeforeReadingOrApplyingEffects(bool batch)
    {
        var reader = new RecordingReader();
        var checkpoints = new RecordingCheckpoints();
        var effects = new List<Uuid>();
        Reactor reactor = batch
            ? new RecordingBatchReactor(checkpoints, effects)
            : new RecordingReactor(checkpoints, effects);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => new ReactorRunner(reader).RunPassAsync(
            reactor, ProjectionCheckpoint.Start, new ProjectionRunOptions { RebuildId = "candidate" }).AsTask());

        Assert.Equal(0, reader.Reads);
        Assert.Empty(effects);
        Assert.Equal(0, checkpoints.Writes);
    }

    sealed class RecordingReader : IDomainEventReader
    {
        internal int Reads { get; private set; }

        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Reads++;
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Reads++;
            ct.ThrowIfCancellationRequested();
            var id = Uuid.CreateVersion4();
            var change = new ValueChanged(1);
            change.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), id, 1, DateTimeOffset.UtcNow));
            await Task.CompletedTask;
            yield return new DomainEventRecord(new EventStreamAddress("test", "reactors", id.ToString()),
                change, 0, new EventCursor("1"));
        }
    }

    sealed class RecordingCheckpoints : IProjectionCheckpointStore
    {
        internal int Writes { get; private set; }

        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
            => ValueTask.FromResult(ProjectionCheckpoint.Start);

        public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint,
            CancellationToken ct = default)
        {
            Writes++;
            return ValueTask.CompletedTask;
        }
    }

    sealed class RecordingReactor(IProjectionCheckpointStore checkpoints, List<Uuid> effects)
        : Reactor(checkpoints, EventStreamPattern.ForPattern("test", "reactors"))
    {
        protected override ValueTask<Result> ReactToEventAsync(DomainEventRecord record, IExecutionContext context,
            CancellationToken ct)
        {
            effects.Add(record.Event.Metadata.EventId);
            return ValueTask.FromResult(Result.Success);
        }
    }

    sealed class RecordingBatchReactor(IProjectionCheckpointStore checkpoints, List<Uuid> effects)
        : BatchReactor(checkpoints, EventStreamPattern.ForPattern("test", "reactors"))
    {
        protected override ValueTask<Result> ReactBatchAsync(IReadOnlyList<IReactorContext> contexts,
            CancellationToken ct)
        {
            effects.AddRange(contexts.Select(context => context.Source.Event.Metadata.EventId));
            return ValueTask.FromResult(Result.Success);
        }
    }
}

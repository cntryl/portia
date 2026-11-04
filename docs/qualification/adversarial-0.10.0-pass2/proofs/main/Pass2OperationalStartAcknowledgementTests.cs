namespace Cntryl.Portia;

/// <summary>Lost journal acknowledgements must not duplicate a single durable attempt identity.</summary>
public sealed class Pass2OperationalStartAcknowledgementTests
{
    static readonly OperationalTarget Target = new("operational-reactor", "stream://test/operational/*", OperationalScope.Global);
    static readonly OperationalProcessingSettings Settings = new(512, 4096, 3, TimeSpan.TicksPerMillisecond, TimeSpan.TicksPerSecond);

    /// <summary>A persisted start can be observed after acknowledgement loss without being appended again.</summary>
    [Fact]
    public async Task ShouldRecordOneAttemptAfterStartAcknowledgementIsLost()
    {
        var journal = new LostStartAcknowledgementStore();
        var session = await SessionAsync(journal);
        var records = new List<DomainEventRecord>();
        await foreach (var record in (await OperationalExecutionTests.EventsAsync()).ReadAsync(Target.Identity.Pattern))
            records.Add(record);
        journal.LoseNextStartAcknowledgement = true;

        var attempt = await session.BeginAsync(records, ProjectionCheckpoint.Start, default);

        var persisted = (await journal.ReadAsync(Target))!;
        Assert.Single(persisted.Attempts, candidate => candidate.Id == attempt);
    }

    /// <summary>A reactor reaches its atomic completion receipt despite a lost durable-start acknowledgement.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldCompleteReactorAfterStartAcknowledgementLoss(bool loseAcknowledgement)
    {
        var journal = new LostStartAcknowledgementStore();
        var session = await SessionAsync(journal);
        journal.LoseNextStartAcknowledgement = loseAcknowledgement;
        var effects = new OperationalExecutionTests.Effects();
        var reactor = new OperationalExecutionTests.EffectReactor(journal, effects);
        reactor.BindWorkload(new WorkloadIdentity(Target.Processor), Target.Processor);

        var checkpoint = await new ReactorRunner(await OperationalExecutionTests.EventsAsync())
        { Operations = session }.RunAsync(reactor, ProjectionCheckpoint.Start);

        Assert.Equal("2", checkpoint.Cursor.Value);
        Assert.Equal(checkpoint, await journal.LoadAsync(Target.Identity));
        Assert.Equal(2, effects.Executions.Count);
        Assert.Equal("committed", Assert.Single((await journal.ReadAsync(Target))!.Attempts).Outcome);
    }

    static async Task<OperationalSession> SessionAsync(LostStartAcknowledgementStore journal)
    {
        var session = new OperationalSession(journal,
            new OperationalOptions { JournalRetryDelay = TimeSpan.FromMilliseconds(1) },
            new EmptyApplicationTopology(), TimeProvider.System);
        await session.AcquireAsync(Target, "reactor", Settings, ProjectionCheckpoint.Start, default);
        return session;
    }

    sealed class LostStartAcknowledgementStore : IOperationalStore, IOperationalReactionCheckpointStore
    {
        readonly MemoryOperationalStore _inner = new();
        internal bool LoseNextStartAcknowledgement { get; set; }

        public ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default)
            => _inner.ReadAsync(target, ct);

        public ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope,
            CancellationToken ct = default) => _inner.ListAsync(scope, ct);

        public async ValueTask<bool> CompareExchangeAsync(OperationalProcessorState replacement,
            long expectedRevision, Guid expectedOwner, CancellationToken ct = default)
        {
            var result = await _inner.CompareExchangeAsync(replacement, expectedRevision, expectedOwner, ct);
            if (result && LoseNextStartAcknowledgement && replacement.Attempts.Any(attempt => attempt.Outcome == "running"))
            {
                LoseNextStartAcknowledgement = false;
                throw new IOException("the journal committed the attempt start but its acknowledgement was lost");
            }
            return result;
        }

        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
            => _inner.LoadAsync(identity, ct);

        public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint,
            CancellationToken ct = default) => _inner.SaveAsync(identity, checkpoint, ct);

        public ValueTask<bool> CommitAsync(OperationalProcessorState replacement, long expectedRevision,
            Guid ownerSession, Guid attemptId, string? expectedCheckpoint, string? nextCheckpoint,
            CancellationToken ct = default)
            => _inner.CommitAsync(replacement, expectedRevision, ownerSession, attemptId,
                expectedCheckpoint, nextCheckpoint, ct);
    }
}

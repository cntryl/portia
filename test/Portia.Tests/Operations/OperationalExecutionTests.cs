namespace Cntryl.Portia;

/// <summary>Exercises at-least-once execution with durable evidence and explicit recovery.</summary>
public sealed partial class OperationalExecutionTests
{
    static readonly OperationalTarget Target = new("operational-reactor", "stream://test/operational/*", OperationalScope.Global);
    static readonly OperationalProcessingSettings Settings = new(512, 4096, 1, TimeSpan.TicksPerMillisecond, TimeSpan.TicksPerSecond);

    /// <summary>A restart preserves the block, and approved retry preserves effects and refreshes execution IDs.</summary>
    [Fact]
    public async Task ShouldRecoverOriginalBatchWithoutDuplicatingEarlierEffect()
    {
        var journal = new FitzOperationalStore(new FakeKvClient(), "kv://test/operations/progress");
        var events = await EventsAsync();
        var effects = new Effects { Fail = true };
        var first = await SessionAsync(journal);
        var runner = new ReactorRunner(events) { Operations = first };
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() => runner.RunAsync(Reactor(journal, effects), ProjectionCheckpoint.Start).AsTask());
        Assert.Single(effects.Ids);
        var failed = (await journal.ReadAsync(Target))!;
        Assert.NotNull(failed.Block);
        Assert.Equal(2, Assert.Single(failed.Attempts).Sources.Count);
        Assert.Equal(ProjectionCheckpoint.Start, await journal.LoadAsync(Target.Identity));

        var restarted = await SessionAsync(journal);
        Assert.False(await restarted.PrepareAsync(ProjectionCheckpoint.Start, default));
        var api = Api(journal);
        var agent = TestOperationalAuthorizer.Actor();
        var plan = await api.PreviewRepairAsync(agent, Target);
        Assert.Equal("retry", plan.Action);
        _ = await api.ApproveRepairAsync(TestOperationalAuthorizer.Actor(true), Target, plan.Id);
        var queued = await api.ExecuteRepairAsync(agent, Target, plan.Id);
        Assert.Equal("queued", queued.Status);
        Assert.Equal(2, effects.Executions.Count);
        Assert.True(await restarted.PrepareAsync(ProjectionCheckpoint.Start, default));
        effects.Fail = false;
        var committed = await new ReactorRunner(events) { Operations = restarted }.RunPassAsync(Reactor(journal, effects),
            ProjectionCheckpoint.Start, restarted.BoundOptions(ProjectionRunOptions.Default));
        Assert.Equal("2", committed.Cursor.Value);
        Assert.Equal(2, effects.Ids.Count);
        Assert.Equal(4, effects.Executions.Distinct().Count());
        var verification = await api.VerifyRepairAsync(agent, Target, plan.Id);
        Assert.Equal("committed", verification.Status);
        Assert.Equal(committed.Cursor.Value, verification.Checkpoint);
        var evidence = (await journal.ReadAsync(Target))!;
        Assert.Null(evidence.Block);
        Assert.Equal(2, evidence.Attempts.Count);
        Assert.Contains(evidence.Attempts[^1].Trace, e => e.Kind == "handler" && e.Outcome == "success");
        Assert.DoesNotContain("private failure", System.Text.Json.JsonSerializer.Serialize(evidence,
            FitzJsonContext.Default.OperationalProcessorState), StringComparison.Ordinal);
    }

    /// <summary>A lost commit acknowledgement is resolved by reading the atomic receipt.</summary>
    [Fact]
    public async Task ShouldResolveLostReactorCommitAcknowledgementWithoutReplay()
    {
        var journal = new MemoryOperationalStore();
        var checkpoints = new LostAcknowledgement(journal);
        var session = await SessionAsync(journal);
        var effects = new Effects();
        _ = await new ReactorRunner(await EventsAsync()) { Operations = session }
            .RunAsync(Reactor(checkpoints, effects), ProjectionCheckpoint.Start);
        Assert.Equal(2, effects.Executions.Count);
        Assert.Equal(1, checkpoints.Commits);
        Assert.Equal("committed", Assert.Single((await journal.ReadAsync(Target))!.Attempts).Outcome);
    }

    /// <summary>A changed source set is rejected before any recovery handler starts.</summary>
    [Fact]
    public async Task ShouldRejectChangedRecoverySourcesBeforeInvocation()
    {
        var journal = new MemoryOperationalStore();
        var first = await SessionAsync(journal);
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(async () => await new ReactorRunner(await EventsAsync())
        { Operations = first }.RunAsync(Reactor(journal, new Effects { Fail = true }), ProjectionCheckpoint.Start).AsTask());
        var session = await SessionAsync(journal);
        var api = Api(journal);
        var actor = TestOperationalAuthorizer.Actor(true);
        var plan = await api.PreviewRepairAsync(actor, Target);
        _ = await api.ApproveRepairAsync(actor, Target, plan.Id);
        _ = await api.ExecuteRepairAsync(actor, Target, plan.Id);
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, default));
        var effects = new Effects();
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(async () => await new ReactorRunner(await EventsAsync())
        { Operations = session }.RunAsync(Reactor(journal, effects), ProjectionCheckpoint.Start).AsTask());
        Assert.Empty(effects.Executions);
        Assert.NotNull((await journal.ReadAsync(Target))!.Block);
    }

    /// <summary>Interrupted evidence survives automatic replay with stable effect identities.</summary>
    [Fact]
    public async Task ShouldPreserveInterruptedAttemptsWhenAutomaticReplaySucceeds()
    {
        var journal = new MemoryOperationalStore();
        var events = await EventsAsync();
        var records = new List<DomainEventRecord>();
        await foreach (var record in events.ReadAsync(Target.Identity.Pattern, EventCursor.Start, default))
            records.Add(record);
        var interrupted = await SessionAsync(journal);
        var originalId = await interrupted.BeginAsync(records, ProjectionCheckpoint.Start, default);
        var resumed = await SessionAsync(journal);
        var effects = new Effects();
        _ = await new ReactorRunner(events) { Operations = resumed }.RunAsync(Reactor(journal, effects), ProjectionCheckpoint.Start);
        var state = (await journal.ReadAsync(Target))!;
        Assert.Equal("interrupted", state.Attempts.Single(a => a.Id == originalId).Outcome);
        Assert.Equal("committed", state.Attempts[^1].Outcome);
        Assert.NotEqual(interrupted.State.OwnerSession, state.OwnerSession);
    }

    /// <summary>Fitz stores starts synchronously and releases every journal lock before application code.</summary>
    [Fact]
    public async Task ShouldPersistStartBeforeInvokingOutsideTransactions()
    {
        var client = new FakeKvClient();
        var journal = new FitzOperationalStore(client, "kv://test/operations/progress");
        var session = await SessionAsync(journal);
        var effects = new Effects
        {
            Before = () =>
        {
            Assert.Empty(client.WriteLocks);
            Assert.All(client.Transactions.Where(t => t.Commits > 0), t => Assert.Equal(KvDurability.Sync, t.Durability));
            Assert.Contains(client.Committed.Values, bytes => System.Text.Encoding.UTF8.GetString(bytes)
                .Contains("\"outcome\":\"running\"", StringComparison.Ordinal));
        }
        };
        _ = await new ReactorRunner(await EventsAsync()) { Operations = session }
            .RunAsync(Reactor(journal, effects), ProjectionCheckpoint.Start);
        Assert.Equal(2, effects.Ids.Count);
    }

    /// <summary>Required journal outages pause all handler invocation until start evidence can persist.</summary>
    [Fact]
    public async Task ShouldPauseInvocationWhileRequiredJournalIsUnavailable()
    {
        var journal = new MemoryOperationalStore();
        var clock = new ManualTestClock();
        var session = new OperationalSession(journal, new OperationalOptions(), new EmptyApplicationTopology(), clock);
        await session.AcquireAsync(Target, "reactor", Settings, ProjectionCheckpoint.Start, default);
        var effects = new Effects();
        journal.Unavailable = true;
        var pending = new ReactorRunner(await EventsAsync()) { Operations = session }
            .RunAsync(Reactor(journal, effects), ProjectionCheckpoint.Start).AsTask();
        var delay = await clock.WaitForDelayAsync();
        Assert.Empty(effects.Executions);
        Assert.False(pending.IsCompleted);
        journal.Unavailable = false;
        clock.Advance(delay);
        _ = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, effects.Ids.Count);
    }

    /// <summary>Detached work from an earlier unit cannot appear as a child of the next attempt.</summary>
    [Fact]
    public async Task ShouldKeepDetachedTraceEvidenceOutOfTheNextAttempt()
    {
        var journal = new MemoryOperationalStore();
        var session = await SessionAsync(journal);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? detached = null;
        var effects = new Effects
        {
            During = async context =>
            {
                if (context.Trigger.Value == 1)
                {
                    detached = Task.Run(async () =>
                    {
                        await release.Task;
                        ProcessingObservation.Record("request", "detached-request", "earlier-unit", null, "success");
                    });
                }
                else
                {
                    release.SetResult();
                    await detached!;
                }
            }
        };
        _ = await new ReactorRunner(await EventsAsync()) { Operations = session }.RunPassAsync(Reactor(journal, effects),
            ProjectionCheckpoint.Start, new ProjectionRunOptions { MaxBatchSize = 1 });
        Assert.Equal(2, session.State.Attempts.Count);
        Assert.All(session.State.Attempts, attempt =>
        {
            Assert.DoesNotContain(attempt.Trace, entry => entry.Component == "detached-request");
            Assert.Contains("detached work unknown", attempt.Coverage, StringComparison.Ordinal);
        });
    }

    static PortiaOperations Api(IOperationalStore store) => new(store, new OperationalOptions(),
        new EmptyApplicationTopology(), new TestOperationalAuthorizer());

    static async Task<OperationalSession> SessionAsync(IOperationalStore store)
    {
        var session = new OperationalSession(store, new OperationalOptions { JournalRetryDelay = TimeSpan.FromMilliseconds(1) },
            new EmptyApplicationTopology(), TimeProvider.System);
        var checkpoint = await ((IProjectionCheckpointStore)store).LoadAsync(Target.Identity);
        await session.AcquireAsync(Target, "reactor", Settings, checkpoint, default);
        return session;
    }

    static EffectReactor Reactor(IProjectionCheckpointStore checkpoints, Effects effects)
    {
        var reactor = new EffectReactor(checkpoints, effects);
        reactor.BindWorkload(new WorkloadIdentity(Target.Processor), Target.Processor);
        return reactor;
    }

    internal static async Task<InMemoryEventStore> EventsAsync()
    {
        var events = new InMemoryEventStore();
        var id = Uuid.CreateVersion4();
        DomainEvent[] changes = [new ValueChanged(1), new ValueChanged(2)];
        for (var i = 0; i < changes.Length; i++)
            changes[i].AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), id, (ulong)i + 1, DateTimeOffset.UtcNow));
        await events.AppendAsync(new EventStreamAddress("test", "operational", id.ToString()), 0, changes);
        return events;
    }

    internal sealed class Effects
    {
        internal HashSet<Uuid> Ids { get; } = [];
        internal List<Uuid> Executions { get; } = [];
        internal bool Fail { get; set; }
        internal Action? Before { get; init; }
        internal Func<IReactorContext<ValueChanged>, Task>? During { get; init; }
    }

    internal sealed partial class EffectReactor(IProjectionCheckpointStore checkpoints, Effects effects)
        : BatchReactor(checkpoints, Target.Identity.Pattern), IReactorHandler<ValueChanged>
    {
        public async ValueTask<Result> HandleAsync(IReactorContext<ValueChanged> context, CancellationToken ct)
        {
            effects.Before?.Invoke();
            if (effects.During is { } during)
                await during(context);
            effects.Executions.Add(context.ExecutionId);
            if (effects.Fail && context.Trigger.Value == 2)
                return Result.Failure(new RequestError(RequestErrorKind.Validation, "private failure"));
            _ = effects.Ids.Add(CreateEffectId(context, "effect"));
            return Result.Success;
        }
    }

    sealed class LostAcknowledgement(MemoryOperationalStore store) : IOperationalReactionCheckpointStore
    {
        internal int Commits { get; private set; }
        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default) => store.LoadAsync(identity, ct);
        public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default) => throw new NotSupportedException();
        public async ValueTask<bool> CommitAsync(OperationalProcessorState replacement, long expectedRevision, Guid ownerSession,
            Guid attemptId, string? expectedCheckpoint, string? nextCheckpoint, CancellationToken ct = default)
        {
            var result = await store.CommitAsync(replacement, expectedRevision, ownerSession, attemptId, expectedCheckpoint, nextCheckpoint, ct);
            Commits++;
            throw new IOException(result ? "acknowledgement lost" : "unexpected conflict");
        }
    }
}

namespace Cntryl.Portia;

/// <summary>Projection authority remains in the application repository during operational recovery.</summary>
public sealed partial class OperationalProjectionTests
{
    static readonly OperationalTarget Target = new("operational-projector", "stream://test/operational/*", OperationalScope.Global);
    static readonly OperationalProcessingSettings Settings = new(512, 4096, 1, TimeSpan.TicksPerMillisecond, TimeSpan.TicksPerSecond);

    /// <summary>A confirmed commit followed by cleanup failure resumes without reapplying any event.</summary>
    [Fact]
    public async Task ShouldResumeConfirmedProjectionWithoutReplayingCleanupFailure()
    {
        var journal = new MemoryOperationalStore();
        var native = new NativeStore { FailDispose = true };
        var session = await SessionAsync(journal, ProjectionCheckpoint.Start);
        var events = await OperationalExecutionTests.EventsAsync();
        var failure = await Assert.ThrowsAsync<ProcessorFailureException>(() => new ProjectorRunner(events)
        { Operations = session }.RunAsync(new ProbeProjector(native), ProjectionCheckpoint.Start).AsTask());
        Assert.Equal(ProcessorFailureStage.Disposing, failure.Stage);
        Assert.Equal("committed_cleanup_failed", Assert.Single(session.State.Attempts).Outcome);
        Assert.Equal(1, session.State.ConsecutiveFailures);
        Assert.Equal("2", native.Checkpoint.Cursor.Value);
        Assert.Equal(2, native.Changes);
        var api = new PortiaOperations(journal, new OperationalOptions(), new EmptyApplicationTopology(), new TestOperationalAuthorizer());
        var actor = TestOperationalAuthorizer.Actor(true);
        var plan = await api.PreviewRepairAsync(actor, Target);
        Assert.Equal("resume", plan.Action);
        _ = await api.ApproveRepairAsync(actor, Target, plan.Id);
        _ = await api.ExecuteRepairAsync(actor, Target, plan.Id);
        Assert.True(await session.PrepareAsync(native.Checkpoint, default));
        Assert.Equal("committed", (await api.VerifyRepairAsync(actor, Target, plan.Id)).Status);
        _ = await new ProjectorRunner(events) { Operations = session }.RunAsync(new ProbeProjector(native), native.Checkpoint);
        Assert.Equal(2, native.Changes);
    }

    /// <summary>A lost separate confirmation never causes a native projection checkpoint to rewind.</summary>
    [Fact]
    public async Task ShouldReloadAuthoritativeProgressAfterLostProjectionConfirmation()
    {
        var journal = new MemoryOperationalStore();
        var native = new NativeStore();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.AfterCommit = () => { journal.Unavailable = true; committed.TrySetResult(); };
        var session = await SessionAsync(journal, ProjectionCheckpoint.Start);
        var events = await OperationalExecutionTests.EventsAsync();
        using var interrupted = new CancellationTokenSource();
        var pending = new ProjectorRunner(events) { Operations = session }
            .RunAsync(new ProbeProjector(native), ProjectionCheckpoint.Start, ct: interrupted.Token).AsTask();
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, native.OpenTransactions);
        Assert.False(pending.IsCompleted);
        await interrupted.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        journal.Unavailable = false;
        native.AfterCommit = null;
        var resumed = await SessionAsync(journal, native.Checkpoint);
        Assert.True(await resumed.PrepareAsync(native.Checkpoint, default));
        _ = await new ProjectorRunner(events) { Operations = resumed }.RunAsync(new ProbeProjector(native), native.Checkpoint);
        Assert.Equal(2, native.Changes);
        Assert.Equal("2", native.Checkpoint.Cursor.Value);
        Assert.Equal("indeterminate", Assert.Single(resumed.State.Attempts).Outcome);
    }

    static async Task<OperationalSession> SessionAsync(MemoryOperationalStore store, ProjectionCheckpoint checkpoint)
    {
        var session = new OperationalSession(store, new OperationalOptions { JournalRetryDelay = TimeSpan.FromMilliseconds(1) },
            new EmptyApplicationTopology(), TimeProvider.System);
        await session.AcquireAsync(Target, "projector", Settings, checkpoint, default);
        return session;
    }

    internal sealed partial class ProbeProjector(NativeStore store) : BatchProjector(store, Target.Identity.Pattern, Target.Processor),
        IProjectorHandler<ValueChanged>
    {
        public ValueTask<Result> HandleAsync(IProjectorContext<ValueChanged> context, CancellationToken ct)
        {
            store.Staged++;
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class NativeStore : IProjectionStore
    {
        internal bool FailDispose { get; init; }
        internal Action? AfterCommit { get; set; }
        internal ProjectionCheckpoint Checkpoint { get; private set; }
        internal int Staged { get; set; }
        internal int Changes { get; private set; }
        internal int OpenTransactions { get; private set; }
        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default) => ValueTask.FromResult(Checkpoint);
        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
        {
            Assert.Equal(Checkpoint, context.Checkpoint);
            OpenTransactions++;
            return ValueTask.FromResult<IProjectionBatch>(new Batch(this));
        }
        sealed class Batch(NativeStore store) : IProjectionBatch
        {
            public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
            {
                store.Changes += store.Staged;
                store.Staged = 0;
                store.Checkpoint = checkpoint;
                store.AfterCommit?.Invoke();
                return ValueTask.CompletedTask;
            }
            public ValueTask DisposeAsync()
            {
                store.OpenTransactions--;
                store.Staged = 0;
                return store.FailDispose ? ValueTask.FromException(new IOException("cleanup")) : ValueTask.CompletedTask;
            }
        }
    }
}

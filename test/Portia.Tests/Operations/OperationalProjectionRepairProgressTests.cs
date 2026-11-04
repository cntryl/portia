namespace Cntryl.Portia;

public sealed partial class OperationalProjectionTests
{
    /// <summary>Advanced native progress ends an active retry as unknown until an operator approves resumption.</summary>
    [Fact]
    public async Task ShouldPreserveIndeterminateRepairWhenCommitAcknowledgementIsLost()
    {
        var journal = new MemoryOperationalStore();
        var native = new NativeStore();
        var events = await OperationalExecutionTests.EventsAsync();
        var session = new OperationalSession(journal, new OperationalOptions(), new EmptyApplicationTopology(), TimeProvider.System);
        await session.AcquireAsync(Target, "projector", Settings with { FailureAttemptLimit = 3 }, ProjectionCheckpoint.Start, default);
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() => new ProjectorRunner(events)
        { Operations = session }.RunAsync(new PermanentlyFailingProjector(native), ProjectionCheckpoint.Start).AsTask());
        Assert.Equal(ProjectionCheckpoint.Start, native.Checkpoint);
        var api = new PortiaOperations(TestOperationalAuthorizer.Configuration(), journal, new OperationalOptions(),
            new EmptyApplicationTopology(), new TestOperationalAuthorizer(), null);
        var administrator = TestOperationalAuthorizer.AdministratorActor(true);
        var first = await QueueRepairAsync(api, administrator);
        var sibling = await QueueRepairAsync(api, administrator);
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, default));
        native.AfterCommit = () => throw new IOException("commit acknowledgement lost");

        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() => new ProjectorRunner(events)
        { Operations = session }.RunAsync(new ProbeProjector(native), ProjectionCheckpoint.Start,
            session.BoundOptions(ProjectionRunOptions.Default)).AsTask());

        Assert.Equal("2", native.Checkpoint.Cursor.Value);
        Assert.Equal(2, native.Changes);
        Assert.Equal("running", (await api.VerifyRepairAsync(administrator, Target, first.Id)).Status);
        native.AfterCommit = null;

        Assert.False(await session.PrepareAsync(native.Checkpoint, default));

        Assert.Null(session.ActivePlan);
        Assert.NotNull(session.State.Block);
        Assert.Equal(native.Checkpoint.Cursor.Value, session.State.Checkpoint);
        Assert.Equal("indeterminate", (await api.VerifyRepairAsync(administrator, Target, first.Id)).Status);
        Assert.Equal("invalidated", (await api.VerifyRepairAsync(administrator, Target, sibling.Id)).Status);
        var resume = await QueueRepairAsync(api, administrator);
        Assert.Equal("resume", resume.Action);
        Assert.True(await session.PrepareAsync(native.Checkpoint, default));
        Assert.Null(session.State.Block);
        _ = await new ProjectorRunner(events) { Operations = session }
            .RunAsync(new ProbeProjector(native), native.Checkpoint);
        Assert.Equal(2, native.Changes);
        Assert.Equal("indeterminate", (await api.VerifyRepairAsync(administrator, Target, first.Id)).Status);
    }

    static async ValueTask<RepairPlan> QueueRepairAsync(PortiaOperations api, System.Security.Claims.ClaimsPrincipal administrator)
    {
        var plan = await api.PreviewRepairAsync(administrator, Target);
        _ = await api.ApproveRepairAsync(administrator, Target, plan.Id);
        _ = await api.ExecuteRepairAsync(administrator, Target, plan.Id);
        return plan;
    }

    sealed class PermanentlyFailingProjector(NativeStore store)
        : BatchProjector(store, Target.Identity.Pattern, Target.Processor)
    {
        protected override ValueTask<Result> ProjectBatchAsync(IReadOnlyList<DomainEventRecord> records,
            IProjectorContext context, CancellationToken ct)
            => ValueTask.FromResult(Result.Failure(RequestError.Validation("required processing is incomplete")));
    }
}

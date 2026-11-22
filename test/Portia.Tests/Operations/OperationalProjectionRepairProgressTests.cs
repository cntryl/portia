namespace Cntryl.Portia;

public sealed partial class OperationalProjectionTests
{
    /// <summary>Advanced native progress ends an active retry as unknown until an operator approves resumption.</summary>
    [Fact]
    public async Task ShouldPreserveIndeterminateRepairGivenLostCommitAcknowledgement()
    {
        // Arrange
        var journal = new MemoryOperationalStore();
        var native = new NativeStore();
        var events = await OperationalExecutionTests.EventsAsync();
        var session = new OperationalSession(journal, new OperationalOptions(), new EmptyApplicationTopology(),
            TimeProvider.System);

        // Act
        await session.AcquireAsync(Target, "projector", Settings with { FailureAttemptLimit = 3 },
            ProjectionCheckpoint.Start, TestContext.Current.CancellationToken);

        // Assert
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() =>
            new ProjectorRunner(events)
                .RunAsync(session, new PermanentlyFailingProjector(native), ProjectionCheckpoint.Start, ct: TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(ProjectionCheckpoint.Start, native.Checkpoint);
        var api = new PortiaOperations(TestOperationalAuthorizer.Configuration(), journal, new OperationalOptions(),
            new EmptyApplicationTopology(), new TestOperationalAuthorizer(), null);
        var administrator = TestOperationalAuthorizer.AdministratorActor(true);
        var first = await QueueRepairAsync(api, administrator);
        var sibling = await QueueRepairAsync(api, administrator);
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, TestContext.Current.CancellationToken));
        native.AfterCommit = () => throw new IOException("commit acknowledgement lost");

        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() => new ProjectorRunner(events).RunAsync(session,
            new ProbeProjector(native), ProjectionCheckpoint.Start,
            session.BoundOptions(ProjectionRunOptions.Default), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("2", native.Checkpoint.Cursor.Value);
        Assert.Equal(2, native.Changes);
        Assert.Equal("running", (await api.VerifyRepairAsync(administrator, Target, first.Id, TestContext.Current.CancellationToken)).Status);
        native.AfterCommit = null;

        Assert.False(await session.PrepareAsync(native.Checkpoint, TestContext.Current.CancellationToken));

        Assert.Null(session.ActivePlan);
        Assert.NotNull(session.State.Block);
        Assert.Equal(native.Checkpoint.Cursor.Value, session.State.Checkpoint);
        Assert.Equal("indeterminate", (await api.VerifyRepairAsync(administrator, Target, first.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("invalidated", (await api.VerifyRepairAsync(administrator, Target, sibling.Id, TestContext.Current.CancellationToken)).Status);
        var resume = await QueueRepairAsync(api, administrator);
        Assert.Equal("resume", resume.Action);
        Assert.True(await session.PrepareAsync(native.Checkpoint, TestContext.Current.CancellationToken));
        Assert.Null(session.State.Block);
        _ = await new ProjectorRunner(events)
            .RunAsync(session, new ProbeProjector(native), native.Checkpoint, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, native.Changes);
        Assert.Equal("indeterminate", (await api.VerifyRepairAsync(administrator, Target, first.Id, TestContext.Current.CancellationToken)).Status);
    }

    /// <summary>A repair whose last commit response is lost must remain indeterminate.</summary>
    [Fact]
    public async Task ShouldPreserveRepairUncertaintyGivenLostAcknowledgementAtAttemptLimit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var journal = new MemoryOperationalStore();
        var native = new NativeStore();
        var runner = new ProjectorRunner(await OperationalExecutionTests.EventsAsync());
        var topology = new EmptyApplicationTopology();
        var options = new OperationalOptions();
        var session = new OperationalSession(journal, options, topology, TimeProvider.System);
        await session.AcquireAsync(Target, "projector", Settings with { FailureAttemptLimit = 1 },
            ProjectionCheckpoint.Start, ct);
        var initial = await Record.ExceptionAsync(() =>
            runner.RunAsync(session, new PermanentlyFailingProjector(native), ProjectionCheckpoint.Start, ct: ct)
                .AsTask());
        Assert.IsType<ProcessorFailureException>(initial);
        var api = new PortiaOperations(TestOperationalAuthorizer.Configuration(), journal, options, topology,
            new TestOperationalAuthorizer(), null);
        var administrator = TestOperationalAuthorizer.AdministratorActor(true);
        var plan = await QueueRepairAsync(api, administrator);
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, ct));
        native.AfterCommit = () => throw new IOException("commit acknowledgement lost");

        // Act
        var result = await Record.ExceptionAsync(() =>
            runner.RunAsync(session, new ProbeProjector(native), ProjectionCheckpoint.Start,
                session.BoundOptions(ProjectionRunOptions.Default), ct).AsTask());
        _ = await session.PrepareAsync(native.Checkpoint, ct);
        var repair = await api.VerifyRepairAsync(administrator, Target, plan.Id, ct);

        // Assert
        Assert.IsType<ProcessorFailureException>(result);
        Assert.Equal("2", native.Checkpoint.Cursor.Value);
        Assert.Equal(2, native.Changes);
        Assert.Equal(OperationalRepairStatuses.Indeterminate, repair.Status);
    }

    static async ValueTask<RepairPlan> QueueRepairAsync(PortiaOperations api,
        System.Security.Claims.ClaimsPrincipal administrator)
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

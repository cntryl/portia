namespace Cntryl.Portia;

public sealed partial class OperationalExecutionTests
{
    /// <summary>Native progress invalidates queued plans without declaring an unknown repair successful.</summary>
    [Fact]
    public async Task ShouldInvalidateQueuedRepairsGivenAuthoritativeCheckpointChanges()
    {
        // Arrange
        var journal = new MemoryOperationalStore();
        var session = await SessionAsync(journal);

        // Act
        var events = await EventsAsync();

        // Assert
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() =>
            new ReactorRunner(events)
                .RunAsync(session, Reactor(journal, new Effects { Fail = true }), ProjectionCheckpoint.Start, ct: TestContext.Current.CancellationToken).AsTask());
        var api = Api(journal);
        var administrator = TestOperationalAuthorizer.AdministratorActor(true);
        var queued = await QueueRepairAsync(api, administrator);
        var unknown = await QueueRepairAsync(api, administrator);
        var running = await QueueRepairAsync(api, administrator);
        var state = (await journal.ReadAsync(Target, TestContext.Current.CancellationToken))!;
        Assert.True(await journal.CompareExchangeAsync(state with
        {
            Revision = state.Revision + 1,
            Repairs = state.Repairs.Select(repair => repair.PlanId == unknown.Id
                ? repair with { Status = "indeterminate" }
                : repair.PlanId == running.Id
                    ? repair with { Status = "running" }
                    : repair).ToArray()
        }, state.Revision, state.OwnerSession, TestContext.Current.CancellationToken));
        var checkpoint = new ProjectionCheckpoint(new EventCursor("2"));
        await journal.SaveAsync(Target.Identity, checkpoint, TestContext.Current.CancellationToken);

        Assert.False(await session.PrepareAsync(checkpoint, TestContext.Current.CancellationToken));

        var current = (await journal.ReadAsync(Target, TestContext.Current.CancellationToken))!;
        Assert.Equal(checkpoint.Cursor.Value, current.Checkpoint);
        Assert.NotNull(current.Block);
        Assert.Equal("invalidated", (await api.VerifyRepairAsync(administrator, Target, queued.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("indeterminate", (await api.VerifyRepairAsync(administrator, Target, unknown.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("indeterminate", (await api.VerifyRepairAsync(administrator, Target, running.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("indeterminate", Assert.Single(current.Attempts).Outcome);
    }

    /// <summary>A transient recovery failure keeps the same approved repair and its queued sibling until success.</summary>
    [Fact]
    public async Task ShouldKeepQueuedRepairGivenApprovedRetryRemainsIncomplete()
    {
        // Arrange
        var journal = new MemoryOperationalStore();
        var session = new OperationalSession(journal, new OperationalOptions(), new EmptyApplicationTopology(),
            TimeProvider.System);
        await session.AcquireAsync(Target, "reactor", Settings with { FailureAttemptLimit = 3 },
            ProjectionCheckpoint.Start, TestContext.Current.CancellationToken);
        var events = await EventsAsync();
        var unavailable = false;

        // Act
        var effects = new Effects
        {
            Fail = true,
            Before = () =>
            {
                if (unavailable)
                    throw new IOException("offline");
            }
        };

        // Assert
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() =>
            new ReactorRunner(events).RunAsync(session, Reactor(journal, effects), ProjectionCheckpoint.Start, ct: TestContext.Current.CancellationToken)
                .AsTask());
        var originalBlock = session.State.Block;
        var api = Api(journal);
        var administrator = TestOperationalAuthorizer.AdministratorActor(true);
        var first = await QueueRepairAsync(api, administrator);
        var second = await QueueRepairAsync(api, administrator);
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, TestContext.Current.CancellationToken));
        effects.Fail = false;
        unavailable = true;

        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() => new ReactorRunner(events).RunPassAsync(session,
            Reactor(journal, effects), ProjectionCheckpoint.Start,
            session.BoundOptions(ProjectionRunOptions.Default), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(originalBlock, session.State.Block);
        Assert.Equal("running", (await api.VerifyRepairAsync(administrator, Target, first.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("queued", (await api.VerifyRepairAsync(administrator, Target, second.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ProjectionCheckpoint.Start, await journal.LoadAsync(Target.Identity, TestContext.Current.CancellationToken));
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, TestContext.Current.CancellationToken));
        unavailable = false;
        _ = await new ReactorRunner(events).RunPassAsync(session, Reactor(journal, effects),
            ProjectionCheckpoint.Start, session.BoundOptions(ProjectionRunOptions.Default), TestContext.Current.CancellationToken);
        Assert.Equal("committed", (await api.VerifyRepairAsync(administrator, Target, first.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("invalidated", (await api.VerifyRepairAsync(administrator, Target, second.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Contains(session.State.Attempts, attempt => attempt.Outcome == "failed" && attempt.IsTransient == true);
    }

    /// <summary>A failed recovery invalidates stale queued plans while preserving the new unresolved block.</summary>
    [Fact]
    public async Task ShouldPreserveNewBlockGivenApprovedRetryFails()
    {
        // Arrange
        var journal = new MemoryOperationalStore();
        var session = await SessionAsync(journal);
        var events = await EventsAsync();

        // Act
        var effects = new Effects { Fail = true };

        // Assert
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() =>
            new ReactorRunner(events).RunAsync(session, Reactor(journal, effects), ProjectionCheckpoint.Start, ct: TestContext.Current.CancellationToken)
                .AsTask());
        var originalBlock = session.State.Block;
        var api = Api(journal);
        var administrator = TestOperationalAuthorizer.AdministratorActor(true);
        var first = await QueueRepairAsync(api, administrator);
        var second = await QueueRepairAsync(api, administrator);
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, TestContext.Current.CancellationToken));

        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() => new ReactorRunner(events).RunPassAsync(session,
            Reactor(journal, effects), ProjectionCheckpoint.Start,
            session.BoundOptions(ProjectionRunOptions.Default), TestContext.Current.CancellationToken).AsTask());

        Assert.NotNull(session.State.Block);
        Assert.NotEqual(originalBlock, session.State.Block);
        Assert.Equal("failed", (await api.VerifyRepairAsync(administrator, Target, first.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("invalidated", (await api.VerifyRepairAsync(administrator, Target, second.Id, TestContext.Current.CancellationToken)).Status);
        Assert.False(await session.PrepareAsync(ProjectionCheckpoint.Start, TestContext.Current.CancellationToken));
        Assert.Equal(ProjectionCheckpoint.Start, await journal.LoadAsync(Target.Identity, TestContext.Current.CancellationToken));
        Assert.Equal(2, session.State.Attempts.Count);
        Assert.Equal(session.State.Attempts[^1].Id, session.State.Block!.AttemptId);
    }

    static async ValueTask<RepairPlan> QueueRepairAsync(
        PortiaOperations api,
        System.Security.Claims.ClaimsPrincipal administrator
    )
    {
        var plan = await api.PreviewRepairAsync(administrator, Target);
        _ = await api.ApproveRepairAsync(administrator, Target, plan.Id);
        _ = await api.ExecuteRepairAsync(administrator, Target, plan.Id);
        return plan;
    }
}

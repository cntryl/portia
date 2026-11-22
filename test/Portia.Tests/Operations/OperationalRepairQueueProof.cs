namespace Cntryl.Portia;

public sealed partial class OperationalExecutionTests
{
    /// <summary>Resolving one failed block invalidates other queued plans for that same failure.</summary>
    [Fact]
    public async Task ShouldInvalidateOtherQueuedRepairsGivenFailureResolved()
    {
        // Arrange
        var journal = new MemoryOperationalStore();
        var events = await EventsAsync();
        var effects = new Effects { Fail = true };

        // Act
        var session = await SessionAsync(journal);

        // Assert
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() =>
            new ReactorRunner(events).RunAsync(session, Reactor(journal, effects), ProjectionCheckpoint.Start, ct: TestContext.Current.CancellationToken)
                .AsTask());
        var api = Api(journal);
        var administrator = TestOperationalAuthorizer.AdministratorActor(true);
        var first = await api.PreviewRepairAsync(administrator, Target, TestContext.Current.CancellationToken);
        var second = await api.PreviewRepairAsync(administrator, Target, TestContext.Current.CancellationToken);
        _ = await api.ApproveRepairAsync(administrator, Target, first.Id, TestContext.Current.CancellationToken);
        _ = await api.ApproveRepairAsync(administrator, Target, second.Id, TestContext.Current.CancellationToken);
        _ = await api.ExecuteRepairAsync(administrator, Target, first.Id, TestContext.Current.CancellationToken);
        _ = await api.ExecuteRepairAsync(administrator, Target, second.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await journal.ReadAsync(Target, TestContext.Current.CancellationToken))!.Repairs.Count(repair => repair.Status == "queued"));

        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, TestContext.Current.CancellationToken));
        effects.Fail = false;
        var checkpoint = await new ReactorRunner(events).RunPassAsync(session,
            Reactor(journal, effects), ProjectionCheckpoint.Start, session.BoundOptions(ProjectionRunOptions.Default), TestContext.Current.CancellationToken);
        Assert.True(await session.PrepareAsync(checkpoint, TestContext.Current.CancellationToken));

        Assert.Equal("committed", (await api.VerifyRepairAsync(administrator, Target, first.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("invalidated", (await api.VerifyRepairAsync(administrator, Target, second.Id, TestContext.Current.CancellationToken)).Status);
        Assert.DoesNotContain((await journal.ReadAsync(Target, TestContext.Current.CancellationToken))!.Repairs, repair => repair.Status == "queued");
    }
}

namespace Cntryl.Portia;

public sealed partial class OperationalExecutionTests
{
    /// <summary>Resolving one failed block invalidates other queued plans for that same failure.</summary>
    [Fact]
    public async Task ShouldInvalidateOtherQueuedRepairsWhenFailureIsResolved()
    {
        // Arrange
        var journal = new MemoryOperationalStore();
        var events = await EventsAsync();
        var effects = new Effects { Fail = true };

        // Act
        var session = await SessionAsync(journal);

        // Assert
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() => new ReactorRunner(events)
        { Operations = session }.RunAsync(Reactor(journal, effects), ProjectionCheckpoint.Start).AsTask());
        var api = Api(journal);
        var administrator = TestOperationalAuthorizer.AdministratorActor(true);
        var first = await api.PreviewRepairAsync(administrator, Target);
        var second = await api.PreviewRepairAsync(administrator, Target);
        _ = await api.ApproveRepairAsync(administrator, Target, first.Id);
        _ = await api.ApproveRepairAsync(administrator, Target, second.Id);
        _ = await api.ExecuteRepairAsync(administrator, Target, first.Id);
        _ = await api.ExecuteRepairAsync(administrator, Target, second.Id);
        Assert.Equal(2, (await journal.ReadAsync(Target))!.Repairs.Count(repair => repair.Status == "queued"));

        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, default));
        effects.Fail = false;
        var checkpoint = await new ReactorRunner(events) { Operations = session }.RunPassAsync(
            Reactor(journal, effects), ProjectionCheckpoint.Start, session.BoundOptions(ProjectionRunOptions.Default));
        Assert.True(await session.PrepareAsync(checkpoint, default));

        Assert.Equal("committed", (await api.VerifyRepairAsync(administrator, Target, first.Id)).Status);
        Assert.Equal("invalidated", (await api.VerifyRepairAsync(administrator, Target, second.Id)).Status);
        Assert.DoesNotContain((await journal.ReadAsync(Target))!.Repairs, repair => repair.Status == "queued");
    }
}

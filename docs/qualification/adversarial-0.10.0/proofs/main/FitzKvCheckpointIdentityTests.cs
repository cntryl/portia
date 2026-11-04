namespace Cntryl.Portia;

/// <summary>Verifies that accepted source and generation identities cannot alias durable checkpoints.</summary>
public sealed class FitzKvCheckpointIdentityTests
{
    /// <summary>Embedded separators remain data rather than changing another identity field's boundary.</summary>
    [Fact]
    public void ShouldCreateDistinctKeysForDistinctAdmittedCheckpointIdentities()
    {
        var (first, second) = DistinctIdentities();
        Assert.NotEqual(first, second);
        Assert.Equal(FitzKvCheckpoints.WorkloadRoute("kv://review/identity/checkpoints", first),
            FitzKvCheckpoints.WorkloadRoute("kv://review/identity/checkpoints", second));

        Assert.NotEqual(FitzKvCheckpoints.Key(first).ToArray(), FitzKvCheckpoints.Key(second).ToArray());
    }

    /// <summary>A checkpoint saved for one valid source and generation must not appear under another.</summary>
    [Fact]
    public async Task ShouldKeepDistinctAdmittedCheckpointProgressIndependent()
    {
        var (first, second) = DistinctIdentities();
        Assert.NotEqual(first, second);
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://review/identity/checkpoints");
        await store.SaveAsync(first, new ProjectionCheckpoint(new EventCursor("8")));

        Assert.Equal(ProjectionCheckpoint.Start, await store.LoadAsync(second));
    }

    static (CheckpointIdentity First, CheckpointIdentity Second) DistinctIdentities() =>
        (new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "events", "order\0part"), "v2"),
            new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "events", "order"), "part\0v2"));
}

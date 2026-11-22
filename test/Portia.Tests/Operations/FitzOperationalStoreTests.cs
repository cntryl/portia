namespace Cntryl.Portia;

/// <summary>Qualifies synchronous journaling and atomic completion against the Fitz transaction contract.</summary>
public sealed class FitzOperationalStoreTests
{
    static readonly OperationalTarget Target = new("reactor", "stream://tenant/orders/*",
        OperationalScope.ForTenant(new TenantId("tenant")));

    /// <summary>Unfenced writes cannot replace authoritative operational progress.</summary>
    [Fact]
    public async Task ShouldPreserveProgressGivenUnfencedWriteAttempt()
    {
        // Arrange
        var client = new FakeKvClient();
        var checkpoints = new FitzKvCheckpointStore(client, "kv://test/state/progress", KvDurability.Sync);
        var checkpoint = new ProjectionCheckpoint(new EventCursor("opaque/not-a-number:99"));
        await checkpoints.SaveAsync(Target.Identity, checkpoint, TestContext.Current.CancellationToken);
        var operations = new FitzOperationalStore(client, "kv://test/state/progress");

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await operations.SaveAsync(Target.Identity, ProjectionCheckpoint.Start, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(checkpoint, await operations.LoadAsync(Target.Identity, TestContext.Current.CancellationToken));
    }

    /// <summary>A failed commit preserves both progress and running evidence, and rejects stale owners.</summary>
    [Fact]
    public async Task ShouldCommitReceiptAndCheckpointTogetherGivenOperationalState()
    {
        // Arrange
        var client = new FakeKvClient();
        var store = new FitzOperationalStore(client, "kv://test/state/progress");
        var owner = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var attempt = new OperationalAttempt(attemptId, owner, DateTimeOffset.UtcNow, null, "running", null,
            "opaque:next", [new OperationalSource("stream://tenant/orders/order", "event", 0, "opaque:next")], [],
            "unknown", new ApplicationTopology([], [], "unknown"));

        // Act
        var state = new OperationalProcessorState(Target, "reactor", owner, 1, null,
            new OperationalProcessingSettings(1, 1, 3, 1, 1), [attempt], null, [], [], [], 0, DateTimeOffset.UtcNow);

        // Assert
        Assert.True(await store.CompareExchangeAsync(state, 0, Guid.Empty, TestContext.Current.CancellationToken));
        var next = state with
        {
            Revision = 2,
            Checkpoint = "opaque:next",
            Attempts =
            [
                attempt with
                {
                    Outcome = "committed", CompletedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        Assert.False(await store.CommitAsync(next, 1, Guid.NewGuid(), attemptId, null, "opaque:next", TestContext.Current.CancellationToken));
        client.CommitFailure = new IOException("injected commit failure");
        _ = await Assert.ThrowsAsync<IOException>(() =>
            store.CommitAsync(next, 1, owner, attemptId, null, "opaque:next", TestContext.Current.CancellationToken).AsTask());
        client.CommitFailure = null;
        Assert.Equal(ProjectionCheckpoint.Start, await store.LoadAsync(Target.Identity, TestContext.Current.CancellationToken));
        Assert.Equal("running", Assert.Single((await store.ReadAsync(Target, TestContext.Current.CancellationToken))!.Attempts).Outcome);
        Assert.True(await store.CommitAsync(next, 1, owner, attemptId, null, "opaque:next", TestContext.Current.CancellationToken));
        Assert.Equal("opaque:next", (await store.LoadAsync(Target.Identity, TestContext.Current.CancellationToken)).Cursor.Value);
        Assert.Equal("committed", Assert.Single((await store.ReadAsync(Target, TestContext.Current.CancellationToken))!.Attempts).Outcome);
        Assert.All(client.Transactions.Where(t => t.Commits > 0), t => Assert.Equal(KvDurability.Sync, t.Durability));
        Assert.Empty(client.WriteLocks);
    }

    /// <summary>Tenant operational documents use independent Fitz resources.</summary>
    [Fact]
    public async Task ShouldDeriveIndependentResourcesGivenTenantJournals()
    {
        // Arrange
        var client = new FakeKvClient();
        var store = new FitzOperationalStore(client, "kv://test/state/progress");
        _ = await store.ReadAsync(Target, TestContext.Current.CancellationToken);
        var firstRoute = client.LastTransaction.Route;
        var other = Target with
        {
            Pattern = "stream://other/orders/*",
            Scope = OperationalScope.ForTenant(new TenantId("other"))
        };

        // Act
        _ = await store.ReadAsync(other, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEqual(firstRoute, client.LastTransaction.Route);
        Assert.Equal(FitzKvCheckpoints.WorkloadRoute("kv://test/state/progress", Target.Identity), firstRoute);
    }
}

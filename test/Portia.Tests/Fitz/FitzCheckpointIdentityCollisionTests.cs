namespace Cntryl.Portia;

/// <summary>Adversarial proof that accepted checkpoint identities retain independent durable progress.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzCheckpointIdentityCollisionProof(FitzBrokerFixture broker)
{
    /// <summary>NUL is rejected before persistence, and ordinary source and generation identities remain independent.</summary>
    [Fact]
    public async Task ShouldKeepCheckpointsSeparateGivenDistinctAdmittedIdentities()
    {
        // Arrange
        await using var client = await broker.CreateClientAsync();
        var store = new FitzKvCheckpointStore(client.Kv, "kv://portia-review/checkpoint/" + Uuid.CreateVersion4());
        _ = Assert.Throws<ArgumentException>(() => EventStreamPattern.ForPattern("tenant", "events", "order\0part"));
        _ = Assert.Throws<ArgumentException>(() => new CheckpointIdentity("orders",
            EventStreamPattern.ForPattern("tenant", "events", "order"), "part\0v2"));
        var first = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "events", "order-part"), "v2");
        var second = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "events", "order"), "part-v2");
        Assert.NotEqual(first, second);

        // Act
        await store.SaveAsync(first, new ProjectionCheckpoint(new EventCursor("8")));

        // Assert
        Assert.Equal(ProjectionCheckpoint.Start, await store.LoadAsync(second));
    }
}

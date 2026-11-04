namespace Cntryl.Portia;

/// <summary>Adversarial proof that accepted checkpoint identities retain independent durable progress.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzCheckpointIdentityCollisionProof(FitzBrokerFixture broker)
{
    /// <summary>Separators embedded in accepted identity fields must not alias another source and generation.</summary>
    [Fact]
    public async Task ShouldKeepCheckpointsSeparateGivenDistinctAdmittedIdentities()
    {
        // Arrange
        await using var client = await broker.CreateClientAsync();
        var store = new FitzKvCheckpointStore(client.Kv, "kv://portia-review/checkpoint/" + Uuid.CreateVersion4());
        var first = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "events", "order\0part"), "v2");
        var second = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "events", "order"), "part\0v2");
        Assert.NotEqual(first, second);

        // Act
        await store.SaveAsync(first, new ProjectionCheckpoint(new EventCursor("8")));

        // Assert
        Assert.Equal(ProjectionCheckpoint.Start, await store.LoadAsync(second));
    }
}

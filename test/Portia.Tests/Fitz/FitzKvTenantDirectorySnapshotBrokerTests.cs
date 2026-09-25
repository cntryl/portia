namespace Cntryl.Portia;

/// <summary>Checks tenant snapshot persistence against the broker's KV transactions.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzKvTenantDirectorySnapshotBrokerTests(FitzBrokerFixture broker)
{
    /// <summary>A new adapter reads the committed roster, while another pattern stays independent.</summary>
    [Fact]
    public async Task ShouldRoundTripAndIsolateLifecyclePatterns()
    {
        await using var client = await broker.CreateClientAsync();
        var route = "kv://portia-integration/tenants/s" + Uuid.CreateVersion4().ToGuid().ToString("N");
        var pattern = EventStreamPattern.ForPattern("global", "tenants", "registry");
        var other = EventStreamPattern.ForPattern("global", "tenants", "other");
        var saved = new TenantDirectorySnapshot(new EventCursor("42"), [new TenantId("acme")]);
        var writer = new FitzKvTenantDirectorySnapshotStore(client.Kv, route);

        Assert.True(await writer.TrySaveAsync(pattern, null, saved));

        var reader = new FitzKvTenantDirectorySnapshotStore(client.Kv, route);
        var loaded = await reader.LoadAsync(pattern);
        Assert.Equal(saved.Cursor, loaded!.Cursor);
        Assert.Equal(saved.ActiveTenants, loaded.ActiveTenants);
        Assert.Null(await reader.LoadAsync(other));
    }
}

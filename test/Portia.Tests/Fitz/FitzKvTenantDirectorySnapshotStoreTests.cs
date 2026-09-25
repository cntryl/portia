namespace Cntryl.Portia;

/// <summary>Checks conditional tenant snapshot persistence.</summary>
public sealed class FitzKvTenantDirectorySnapshotStoreTests
{
    /// <summary>An older worker cannot replace a newer roster, and patterns remain isolated.</summary>
    [Fact]
    public async Task ShouldKeepNewerRosterWhenAStaleWriterTriesToSave()
    {
        var kv = new FakeKvClient();
        var store = new FitzKvTenantDirectorySnapshotStore(kv, "kv://global/tenants/snapshots");
        var pattern = EventStreamPattern.ForPattern("global", "tenants", "registry");
        var other = EventStreamPattern.ForPattern("global", "tenants", "other");
        var first = new TenantDirectorySnapshot(new EventCursor("1"), [new TenantId("acme")]);
        var second = new TenantDirectorySnapshot(new EventCursor("2"), [new TenantId("beta")]);

        Assert.True(await store.TrySaveAsync(pattern, null, first));
        Assert.True(await store.TrySaveAsync(pattern, first.Cursor, second));
        Assert.False(await store.TrySaveAsync(pattern, first.Cursor, first));
        Assert.Equal(second.Cursor, (await store.LoadAsync(pattern))!.Cursor);
        Assert.Equal([new TenantId("beta")], (await store.LoadAsync(pattern))!.ActiveTenants);
        _ = await store.LoadAsync(other);
        Assert.NotEqual(kv.Transactions[^2].Route, kv.Transactions[^1].Route);

        _ = await store.LoadAsync(pattern);
        await using var held = await kv.BeginAsync(kv.LastTransaction.Route, KvDurability.Sync);
        Assert.False(await store.TrySaveAsync(pattern, second.Cursor,
            new TenantDirectorySnapshot(new EventCursor("3"), [new TenantId("gamma")])));
    }
}

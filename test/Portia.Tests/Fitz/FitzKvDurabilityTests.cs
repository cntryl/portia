using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Applications choose KV persistence independently of transaction atomicity.</summary>
public sealed class FitzKvDurabilityTests
{
    const string Route = "kv://portia/state/durability";
    static readonly EventStreamPattern Pattern = EventStreamPattern.ForPattern("global", "orders");
    static readonly CheckpointIdentity Identity = new("orders", Pattern);

    /// <summary>Reactor checkpoint commits forward the selected persistence mode.</summary>
    [Theory]
    [InlineData(KvDurability.Buffered)]
    [InlineData(KvDurability.Sync)]
    public async Task ShouldUseSelectedDurabilityGivenCheckpointStore(KvDurability durability)
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, Route, durability);
        var checkpoint = new ProjectionCheckpoint(new EventCursor("1"));

        await store.SaveAsync(Identity, checkpoint);

        Assert.Equal(durability, client.LastTransaction.Durability);
        Assert.Equal(checkpoint, await store.LoadAsync(Identity));
    }

    /// <summary>Projection data and progress still commit together for either persistence mode.</summary>
    [Theory]
    [InlineData(KvDurability.Buffered)]
    [InlineData(KvDurability.Sync)]
    public async Task ShouldCommitDataAndCheckpointTogetherGivenSelectedDurability(KvDurability durability)
    {
        var client = new FakeKvClient();
        var store = new Projection(client, durability);
        var checkpoint = new ProjectionCheckpoint(new EventCursor("1"));

        await using (var batch = await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)))
        {
            await store.WriteAsync();
            Assert.Empty(client.Committed);
            await batch.CommitAsync(checkpoint);
        }

        Assert.Equal(durability, client.LastTransaction.Durability);
        Assert.Equal(1, client.LastTransaction.Commits);
        Assert.Equal(new byte[] { 42 }, client.Read("value"u8.ToArray()));
        Assert.Equal(checkpoint, await store.LoadCheckpointAsync(Identity));
    }

    /// <summary>Tenant rosters and their cursors use the configured persistence choice.</summary>
    [Theory]
    [InlineData(KvDurability.Buffered)]
    [InlineData(KvDurability.Sync)]
    public async Task ShouldUseSelectedDurabilityGivenTenantSnapshotStore(KvDurability durability)
    {
        var client = new FakeKvClient();
        var store = new FitzKvTenantDirectorySnapshotStore(client, Route, durability);
        var snapshot = new TenantDirectorySnapshot(new EventCursor("1"), [new TenantId("acme")]);

        Assert.True(await store.TrySaveAsync(Pattern, null, snapshot));

        Assert.Equal(durability, client.LastTransaction.Durability);
        var loaded = await store.LoadAsync(Pattern);
        Assert.Equal(snapshot.Cursor, loaded!.Cursor);
        Assert.Equal(snapshot.ActiveTenants, loaded.ActiveTenants);
    }

    /// <summary>Repeated checkpoint registration retains the selected mode in the resolved store.</summary>
    [Theory]
    [InlineData(KvDurability.Buffered)]
    [InlineData(KvDurability.Sync)]
    public async Task ShouldConfigureResolvedCheckpointDurabilityGivenRepeatedSelection(KvDurability durability)
    {
        var services = new ServiceCollection();
        var client = new FakeKvClient();
        _ = services.AddSingleton<IKvClient>(client);
        _ = services.AddPortia().AddFitz(Configuration(), builder =>
            builder.UseKvCheckpoints(Route, durability).UseKvCheckpoints(Route, durability));
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectionCheckpointStore>();

        await store.SaveAsync(Identity, new ProjectionCheckpoint(new EventCursor("1")));

        Assert.Equal(durability, client.LastTransaction.Durability);
        _ = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IProjectionCheckpointStore));
    }

    /// <summary>The existing builder overload defaults to buffered commits.</summary>
    [Fact]
    public async Task ShouldUseBufferedDurabilityGivenDefaultCheckpointSelection()
    {
        var services = new ServiceCollection();
        var client = new FakeKvClient();
        _ = services.AddSingleton<IKvClient>(client);
        _ = services.AddPortia().AddFitz(Configuration(), builder => builder.UseKvCheckpoints(Route));
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IProjectionCheckpointStore>()
            .SaveAsync(Identity, new ProjectionCheckpoint(new EventCursor("1")));

        Assert.Equal(KvDurability.Buffered, client.LastTransaction.Durability);
    }

    /// <summary>Conflicting persistence choices cannot silently keep the first registration.</summary>
    [Theory]
    [InlineData(KvDurability.Buffered, KvDurability.Sync)]
    [InlineData(KvDurability.Sync, KvDurability.Buffered)]
    public void ShouldRejectConflictingDurabilityGivenRepeatedCheckpointSelection(KvDurability first, KvDurability second)
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddPortia().AddFitz(Configuration(), builder =>
                builder.UseKvCheckpoints(Route, first).UseKvCheckpoints(Route, second)));

        Assert.Contains("durability", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Invalid persistence modes fail before any transaction or checkpoint registration.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void ShouldRejectUnknownDurabilityGivenStoreOrBuilder(int value)
    {
        var durability = (KvDurability)value;
        var client = new FakeKvClient();
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new FitzKvCheckpointStore(client, Route, durability));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new Projection(client, durability));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new FitzKvTenantDirectorySnapshotStore(client, Route, durability));
        var services = new ServiceCollection();
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddPortia().AddFitz(Configuration(), builder => builder.UseKvCheckpoints(Route, durability)));

        Assert.Empty(client.Transactions);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IProjectionCheckpointStore));
    }

    static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Endpoint"] = "ws://127.0.0.1:4090/ws" })
        .Build();

    sealed class Projection(IKvClient client, KvDurability durability)
        : FitzKvProjectionStore(client, Route, "orders", durability)
    {
        public Task WriteAsync() => Transaction.PutAsync("value"u8.ToArray(), new byte[] { 42 });
    }
}

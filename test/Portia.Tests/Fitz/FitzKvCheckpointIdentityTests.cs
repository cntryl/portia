using System.Text;

namespace Cntryl.Portia;

/// <summary>Verifies identity boundaries without changing already persisted ordinary checkpoint keys.</summary>
public sealed class FitzKvCheckpointIdentityTests
{
    /// <summary>The separator used in durable checkpoint keys cannot enter any source segment.</summary>
    /// <param name="segment">The source segment containing NUL.</param>
    [Theory]
    [InlineData("realm")]
    [InlineData("area")]
    [InlineData("resource")]
    public void ShouldRejectNulInStreamIdentitySegments(string segment)
    {
        var realm = segment == "realm" ? "tenant\0other" : "tenant";
        var area = segment == "area" ? "events\0other" : "events";
        var resource = segment == "resource" ? "order\0part" : "order";

        Assert.Equal(segment, Assert.Throws<ArgumentException>(() =>
            EventStreamPattern.ForPattern(realm, area, resource)).ParamName);
        Assert.Equal(segment, Assert.Throws<ArgumentException>(() =>
            new EventStreamAddress(realm, area, resource)).ParamName);
        _ = Assert.Throws<ArgumentException>(() =>
            EventStreamAddress.Parse($"stream://{realm}/{area}/{resource}"));
        if (segment != "realm")
            _ = Assert.Throws<ArgumentException>(() => EventStreamPattern.ForTenant(area, resource));
    }

    /// <summary>Names and generations cannot introduce another durable key field.</summary>
    [Fact]
    public void ShouldRejectNulInCheckpointAndWorkloadNames()
    {
        var pattern = EventStreamPattern.ForPattern("tenant", "events", "order");
        Assert.Equal("componentName", Assert.Throws<ArgumentException>(() =>
            new CheckpointIdentity("orders\0other", pattern)).ParamName);
        Assert.Equal("rebuildId", Assert.Throws<ArgumentException>(() =>
            new CheckpointIdentity("orders", pattern, "part\0v2")).ParamName);
        _ = Assert.Throws<ArgumentException>(() => new WorkloadIdentity("orders\0other"));
        _ = Assert.Throws<ArgumentException>(() => new NamedProjector("orders\0other"));
        _ = Assert.Throws<ArgumentException>(() => new NamedReactor("orders\0other"));
        _ = Assert.Throws<ArgumentException>(() => new ProjectionRunOptions { RebuildId = "part\0v2" }.Validate());
        _ = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddPortia()
            .AddProjector<TestProjector>("orders\0other", WorkloadScope.Global));
    }

    /// <summary>Tenant assignment and parsing reject the same invalid realm before routing or key derivation.</summary>
    [Fact]
    public void ShouldRejectNulInTenantIdentity()
    {
        const string value = "tenant\0other";
        _ = Assert.Throws<ArgumentException>(() => new TenantId(value));
        _ = Assert.Throws<ArgumentException>(() => new TenantId("tenant") with { Value = value });
        Assert.False(TenantId.TryParse(value, null, out _));
        Assert.False(TenantId.TryParse(value.AsSpan(), null, out _));
        _ = Assert.Throws<FormatException>(() => TenantId.Parse(value, null));
    }

    /// <summary>Invalid UTF-16 cannot alias the UTF-8 replacement character in a durable identity.</summary>
    /// <param name="kind">The malformed UTF-16 sequence to construct at runtime.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ShouldRejectMalformedUnicodeIdentities(int kind)
    {
        var invalid = kind switch
        {
            0 => "orders" + (char)0xD800,
            1 => "orders" + (char)0xDC00,
            _ => "orders" + (char)0xD800 + "x"
        };
        var replacement = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(invalid));
        Assert.NotEqual(invalid, replacement);
        var pattern = EventStreamPattern.ForPattern("tenant");
        _ = Assert.Throws<ArgumentException>(() => new CheckpointIdentity(invalid, pattern));
        _ = Assert.Throws<ArgumentException>(() => new CheckpointIdentity("orders", pattern, invalid));
        _ = Assert.Throws<ArgumentException>(() => EventStreamPattern.ForPattern(invalid));
        _ = Assert.Throws<ArgumentException>(() => EventStreamPattern.ForPattern("tenant", invalid));
        _ = Assert.Throws<ArgumentException>(() => EventStreamPattern.ForPattern("tenant", "events", invalid));
        _ = Assert.Throws<ArgumentException>(() => new EventStreamAddress("tenant", "events", invalid));
        _ = Assert.Throws<ArgumentException>(() => new WorkloadIdentity(invalid));
        _ = Assert.Throws<ArgumentException>(() => new NamedProjector(invalid));
        _ = Assert.Throws<ArgumentException>(() => new NamedReactor(invalid));
        _ = Assert.Throws<ArgumentException>(() => new TenantId(invalid));
        Assert.False(TenantId.TryParse(invalid, null, out _));
    }

    /// <summary>Valid Unicode and existing non-NUL controls retain their exact checkpoint byte encoding.</summary>
    [Fact]
    public void ShouldPreserveValidUnicodeAndNonNulControlCharacters()
    {
        const string text = "café-\uD83D\uDE80\n\t";
        var identity = new CheckpointIdentity(text, EventStreamPattern.ForPattern(text, text, text), text);
        var expected = Encoding.UTF8.GetBytes($"checkpoint\0{text}\0stream://{text}/{text}/{text}\0{text}");
        Assert.Equal(expected, FitzKvCheckpoints.Key(identity).ToArray());
        Assert.True(TenantId.TryParse(text, null, out var tenant));
        Assert.Equal(text, tenant.Value);
        Assert.Equal(text, new WorkloadIdentity(text).Name);
    }

    /// <summary>Store construction and query routing reject NUL before opening a native transaction.</summary>
    [Fact]
    public async Task ShouldRejectNulBeforeNativeRouting()
    {
        var client = new FakeKvClient();
        _ = Assert.Throws<ArgumentException>(() => new IdentityRepository(client, "orders\0other"));
        _ = Assert.Throws<ArgumentException>(() => new FitzKvCheckpointStore(client, "kv://review\0other/identity/checkpoints"));
        var repository = new IdentityRepository(client, "orders");
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await repository.ReadAsync("tenant\0other"));
        Assert.Empty(client.Transactions);
    }

    /// <summary>Ordinary identities retain exactly their prior persisted key bytes.</summary>
    /// <param name="generation">The live or named rebuild generation.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("candidate-2")]
    public void ShouldPreserveExistingKeyBytesForOrdinaryIdentity(string? generation)
    {
        var identity = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "events", "order"),
            generation);
        var expected = Encoding.UTF8.GetBytes("checkpoint\0orders\0stream://tenant/events/order\0" + generation);

        Assert.Equal(expected, FitzKvCheckpoints.Key(identity).ToArray());
    }

    /// <summary>Valid source and generation distinctions continue to own independent progress.</summary>
    [Fact]
    public async Task ShouldKeepOrdinaryCheckpointProgressIndependent()
    {
        var first = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "events", "order-part"), "v2");
        var second = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "events", "order"), "part-v2");
        Assert.NotEqual(FitzKvCheckpoints.Key(first).ToArray(), FitzKvCheckpoints.Key(second).ToArray());
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://review/identity/checkpoints");
        await store.SaveAsync(first, new ProjectionCheckpoint(new EventCursor("8")));

        Assert.Equal(ProjectionCheckpoint.Start, await store.LoadAsync(second));
    }

    sealed class IdentityRepository(IKvClient client, string name)
        : FitzKvProjectionStore(client, "kv://review/identity/checkpoints", name)
    {
        internal async ValueTask ReadAsync(string realm)
        {
            await using var transaction = await BeginReadAsync(realm);
        }
    }

    sealed class NamedProjector(string name)
        : Projector(new RecordingProjectionTarget(), EventStreamPattern.ForPattern("tenant"), name);

    sealed class NamedReactor(string name)
        : Reactor(new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("tenant"), name);
}

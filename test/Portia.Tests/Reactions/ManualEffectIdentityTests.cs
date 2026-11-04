using System.Globalization;

namespace Cntryl.Portia;

/// <summary>Preserves legacy effect keys while rejecting identities whose legacy field boundaries alias.</summary>
public sealed class ManualEffectIdentityTests
{
    const string SourceId = "11111111-1111-4111-8111-111111111111";
    const string OtherSourceId = "33333333-3333-4333-8333-333333333333";

    /// <summary>A name cannot consume either this source UUID or a different source UUID.</summary>
    [Theory]
    [InlineData(SourceId)]
    [InlineData(OtherSourceId)]
    public void ShouldRejectManualNameContainingSourceIdentityDelimiter(string embeddedSource)
    {
        var first = new EffectProbe("billing");
        var second = new EffectProbe("billing:" + embeddedSource + ":reserve");
        var context = Context("acme", SourceId);

        var error = Assert.Throws<InvalidOperationException>(() => second.EffectId(context, "audit"));

        Assert.Contains("ambiguous legacy effect-ID delimiter", error.Message, StringComparison.Ordinal);
        Assert.Equal(Uuid.Parse("6c80a189-65e0-5ca1-8473-f0d8e291fa1c", CultureInfo.InvariantCulture),
            first.EffectId(context, "reserve:" + SourceId + ":audit"));
    }

    /// <summary>The terminal delimiter between the name and source also counts as a boundary.</summary>
    [Fact]
    public void ShouldRejectManualNameEndingInSourceIdentity()
    {
        var reactor = new EffectProbe("billing:" + OtherSourceId);

        _ = Assert.Throws<InvalidOperationException>(() => reactor.EffectId(Context("acme", SourceId), "audit"));
    }

    /// <summary>Accepted names retain the exact previously persisted UUID, including ordinary colons.</summary>
    [Theory]
    [InlineData("billing:reserve", "audit", "f7370848-5c4a-52b1-ad43-7363d8e49ab5")]
    [InlineData(SourceId + ":billing", "audit", "b14fe2d0-7f2c-54a6-a215-a4db1a329b6e")]
    [InlineData("billing", "reserve:" + SourceId + ":audit", "6c80a189-65e0-5ca1-8473-f0d8e291fa1c")]
    [InlineData("billing:ABCDEFAB-CDEF-4ABC-8DEF-ABCDEFABCDEF:reserve", "audit", "b0afd3c4-2cea-5247-8f55-71b715d22f51")]
    [InlineData("billing:00000000-0000-0000-0000-000000000000:reserve", "audit", "bc323bb7-59d3-5569-8fea-7761faa6eae9")]
    [InlineData("billing", "通知😀", "4c880aa0-f143-5ece-af7d-17e640c668c4")]
    [InlineData("billing", "audit\0copy", "67eb83d1-7cb1-57cf-94b7-022c2375c6ee")]
    public void ShouldPreserveAcceptedLegacyManualEffectId(string name, string effect, string expected)
    {
        var reactor = new EffectProbe(name);
        var context = Context("acme", SourceId);

        Assert.Equal(Uuid.Parse(expected, CultureInfo.InvariantCulture), reactor.EffectId(context, effect));
        Assert.Equal(reactor.EffectId(context, effect), reactor.EffectId(context, effect));
        Assert.NotEqual(reactor.EffectId(context, effect), reactor.EffectId(context, effect + ":another"));
    }

    /// <summary>A tenant cannot close its legacy wrapper and consume the reactor/source fields.</summary>
    [Fact]
    public void ShouldRejectBoundTenantContainingLegacyClosingMarker()
    {
        var firstTenant = new TenantId("acme");
        var secondTenant = new TenantId("acme } }:billing:" + SourceId + ":reserve");
        var first = BoundProbe("billing", firstTenant);
        var second = BoundProbe("billing", secondTenant);

        var error = Assert.Throws<InvalidOperationException>(() =>
            second.EffectId(Context(secondTenant.Value, OtherSourceId), "audit"));

        Assert.Contains("tenant", error.Message, StringComparison.Ordinal);
        Assert.Equal(Uuid.Parse("c1d0c1f9-135d-5522-b593-bf5333532a2f", CultureInfo.InvariantCulture),
            first.EffectId(Context(firstTenant.Value, SourceId), "reserve } }:billing:" + OtherSourceId + ":audit"));
    }

    /// <summary>Workload names cannot inject the exact legacy name-to-tenant delimiter.</summary>
    [Fact]
    public void ShouldRejectBoundWorkloadNameContainingLegacyFieldDelimiter()
    {
        var reactor = BoundProbe("billing, Tenant = other", new TenantId("acme"));

        var error = Assert.Throws<InvalidOperationException>(() => reactor.EffectId(Context("acme", SourceId), "audit"));

        Assert.Contains("workload name", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Ordinary hosted names and tenants with colons retain their exact persisted derivation.</summary>
    [Fact]
    public void ShouldPreserveAcceptedLegacyBoundEffectId()
    {
        var tenant = new TenantId("acme:west");
        var reactor = BoundProbe("billing:reserve", tenant);

        Assert.Equal(Uuid.Parse("ef1b0d72-44e9-5743-b649-8e8d9bf6e7bb", CultureInfo.InvariantCulture),
            reactor.EffectId(Context(tenant.Value, SourceId), "audit"));
    }

    /// <summary>Malformed text is refused before UTF-8 replacement can alias the replacement character.</summary>
    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void ShouldRejectMalformedUtf16EffectNames(int codeUnit)
    {
        var reactor = new EffectProbe("billing");
        var context = Context("acme", SourceId);
        var malformed = "notify" + new string((char)codeUnit, 1);

        var error = Assert.Throws<ArgumentException>(() => reactor.EffectId(context, malformed));

        Assert.Equal("effectName", error.ParamName);
        Assert.Equal(Uuid.Parse("4b2d3b56-d8f8-5b6b-b5c7-85a4dcdd214e", CultureInfo.InvariantCulture),
            reactor.EffectId(context, "notify\ufffd"));
    }

    static EffectProbe BoundProbe(string name, TenantId tenant)
    {
        var reactor = new EffectProbe(name, true);
        reactor.BindWorkload(new WorkloadIdentity(name, tenant), name);
        return reactor;
    }

    static ReactionExecutionContext Context(string realm, string eventId)
    {
        var ev = new Triggered();
        ev.AttachMetadata(new DomainEventMetadata(Uuid.Parse(eventId, CultureInfo.InvariantCulture),
            Uuid.CreateVersion4(), 1, DateTimeOffset.UnixEpoch));
        var record = new DomainEventRecord(new EventStreamAddress(realm, "events", "source"), ev, 0,
            new EventCursor("1"));
        return new ReactionExecutionContext(record, RequestActor.System);
    }

    sealed record Triggered : DomainEvent;

    sealed class EffectProbe(string name, bool tenantTemplate = false)
        : Reactor(new InMemoryProjectionCheckpointStore(),
            tenantTemplate ? EventStreamPattern.ForTenant("events") : EventStreamPattern.ForPattern("acme", "events"), name)
    {
        public Uuid EffectId(IReactorContext context, string effectName) => CreateEffectId(context, effectName);
    }
}

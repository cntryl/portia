using System.Globalization;

namespace Cntryl.Portia;

/// <summary>Effect identities frame their fields explicitly so valid user names cannot alias.</summary>
public sealed class ManualEffectIdentityTests
{
    const string SourceId = "11111111-1111-4111-8111-111111111111";
    const string OtherSourceId = "33333333-3333-4333-8333-333333333333";

    /// <summary>UUID-like reactor text and effect names remain distinct fields.</summary>
    [Theory]
    [InlineData(SourceId)]
    [InlineData(OtherSourceId)]
    public void ShouldSeparateManualFieldsGivenSourceIdentityInName(string embeddedSource)
    {
        // Arrange
        var first = new EffectProbe("billing");
        var second = new EffectProbe("billing:" + embeddedSource + ":reserve");
        var context = Context("acme", SourceId);

        // Act
        var firstId = first.EffectId(context, "reserve:" + embeddedSource + ":audit");
        var secondId = second.EffectId(context, "audit");

        // Assert
        Assert.NotEqual(firstId, secondId);
    }

    /// <summary>A name ending in a UUID remains valid and cannot consume the source field.</summary>
    [Fact]
    public void ShouldAcceptManualNameGivenNameEndingInSourceIdentity()
    {
        // Arrange
        var reactor = new EffectProbe("billing:" + OtherSourceId);
        var context = Context("acme", SourceId);

        // Act
        var id = reactor.EffectId(context, "audit");

        // Assert
        Assert.NotEqual(default, id);
    }

    /// <summary>Valid names and effect text produce repeatable, distinct identities.</summary>
    [Theory]
    [InlineData("billing:reserve", "audit")]
    [InlineData(SourceId + ":billing", "audit")]
    [InlineData("billing", "reserve:" + SourceId + ":audit")]
    [InlineData("billing", "通知😀")]
    [InlineData("billing", "audit\0copy")]
    public void ShouldProduceStableIdentityGivenValidText(string name, string effect)
    {
        // Arrange
        var reactor = new EffectProbe(name);
        var context = Context("acme", SourceId);

        // Act
        var first = reactor.EffectId(context, effect);
        var replay = reactor.EffectId(context, effect);
        var other = reactor.EffectId(context, effect + ":another");

        // Assert
        Assert.Equal(first, replay);
        Assert.NotEqual(first, other);
    }

    /// <summary>Tenant text cannot consume subsequent reactor, source, or effect fields.</summary>
    [Fact]
    public void ShouldSeparateBoundTenantGivenClosingMarkerText()
    {
        // Arrange
        var firstTenant = new TenantId("acme");
        var secondTenant = new TenantId("acme } }:billing:" + SourceId + ":reserve");
        var first = BoundProbe("billing", firstTenant);
        var second = BoundProbe("billing", secondTenant);
        var firstContext = Context(firstTenant.Value, SourceId);
        var secondContext = Context(secondTenant.Value, OtherSourceId);

        // Act
        var firstId = first.EffectId(firstContext, "reserve } }:billing:" + OtherSourceId + ":audit");
        var secondId = second.EffectId(secondContext, "audit");

        // Assert
        Assert.NotEqual(firstId, secondId);
    }

    /// <summary>Workload names can contain ordinary record-formatting text without field ambiguity.</summary>
    [Fact]
    public void ShouldAcceptBoundWorkloadNameGivenFieldDelimiterText()
    {
        // Arrange
        var reactor = BoundProbe("billing, Tenant = other", new TenantId("acme"));
        var context = Context("acme", SourceId);

        // Act
        var id = reactor.EffectId(context, "audit");

        // Assert
        Assert.NotEqual(default, id);
    }

    /// <summary>Malformed text is refused before UTF-8 replacement can alias a valid replacement character.</summary>
    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void ShouldRejectEffectNameGivenMalformedUtf16(int codeUnit)
    {
        // Arrange
        var reactor = new EffectProbe("billing");
        var context = Context("acme", SourceId);
        var malformed = "notify" + new string((char)codeUnit, 1);

        // Act
        var exception = Record.Exception(() => reactor.EffectId(context, malformed));

        // Assert
        Assert.Equal("effectName", Assert.IsType<ArgumentException>(exception).ParamName);
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

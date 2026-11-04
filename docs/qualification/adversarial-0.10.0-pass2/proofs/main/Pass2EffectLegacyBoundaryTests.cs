using System.Globalization;

namespace Cntryl.Portia;

/// <summary>Additional ambiguity cases for the legacy effect-identity framing.</summary>
public sealed class Pass2EffectLegacyBoundaryTests
{
    /// <summary>A valid tenant containing a legacy closing marker must not consume another tuple's fields.</summary>
    [Fact]
    public void ShouldSeparateBoundTenantEffectIdentitiesContainingLegacyClosingMarker()
    {
        var firstEvent = Uuid.Parse("11111111-1111-4111-8111-111111111111", CultureInfo.InvariantCulture);
        var secondEvent = Uuid.Parse("33333333-3333-4333-8333-333333333333", CultureInfo.InvariantCulture);
        var firstTenant = new TenantId("acme");
        var secondTenant = new TenantId("acme } }:billing:" + firstEvent + ":reserve");
        var first = new EffectProbe();
        first.BindWorkload(new WorkloadIdentity("billing", firstTenant), "billing");
        var second = new EffectProbe();
        second.BindWorkload(new WorkloadIdentity("billing", secondTenant), "billing");

        var firstEffect = first.EffectId(Context(firstTenant, firstEvent),
            "reserve } }:billing:" + secondEvent + ":audit");
        var secondEffect = second.EffectId(Context(secondTenant, secondEvent), "audit");

        Assert.NotEqual(firstEffect, secondEffect);
    }

    /// <summary>Malformed UTF-16 effect text must not alias a different effect after UTF-8 replacement.</summary>
    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void ShouldSeparateMalformedEffectTextFromReplacementCharacter(int codeUnit)
    {
        var ev = Uuid.Parse("11111111-1111-4111-8111-111111111111", CultureInfo.InvariantCulture);
        var reactor = new EffectProbe();
        var context = Context(new TenantId("acme"), ev);
        var malformed = "notify" + new string((char)codeUnit, 1);

        Assert.NotEqual(reactor.EffectId(context, malformed), reactor.EffectId(context, "notify\ufffd"));
    }

    static ReactionExecutionContext Context(TenantId tenant, Uuid eventId)
    {
        var ev = new Triggered();
        ev.AttachMetadata(new DomainEventMetadata(eventId, Uuid.CreateVersion4(), 1, DateTimeOffset.UnixEpoch));
        var record = new DomainEventRecord(new EventStreamAddress(tenant.Value, "events", "source"), ev, 0,
            new EventCursor("1"));
        return new ReactionExecutionContext(record, RequestActor.System);
    }

    sealed record Triggered : DomainEvent;

    sealed class EffectProbe()
        : Reactor(new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForTenant("events"), "billing")
    {
        public Uuid EffectId(IReactorContext context, string effectName) => CreateEffectId(context, effectName);
    }
}

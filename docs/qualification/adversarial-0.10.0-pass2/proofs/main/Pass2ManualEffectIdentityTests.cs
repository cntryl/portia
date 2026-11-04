using System.Globalization;

namespace Cntryl.Portia;

/// <summary>Different supported manual reactor/effect identities must not become the same idempotency key.</summary>
public sealed class Pass2ManualEffectIdentityTests
{
    /// <summary>Admitted separator-bearing names cannot alias a different reactor/effect tuple.</summary>
    [Fact]
    public void ShouldSeparateEffectsWhenManualNamesContainSourceIdentityText()
    {
        var context = Context();
        var eventId = context.Source.Event.Metadata.EventId;
        var first = new EffectProbe("billing");
        var second = new EffectProbe($"billing:{eventId}:reserve");

        var firstEffect = first.EffectId(context, $"reserve:{eventId}:audit");
        var secondEffect = second.EffectId(context, "audit");

        Assert.NotEqual(firstEffect, secondEffect);
    }

    /// <summary>Ordinary distinct reactor and effect names produce distinct stable identifiers.</summary>
    [Fact]
    public void ShouldSeparateOrdinaryManualReactorAndEffectNames()
    {
        var context = Context();
        var first = new EffectProbe("billing");
        var second = new EffectProbe("billing:reserve");

        Assert.Equal(first.EffectId(context, "reserve"), first.EffectId(context, "reserve"));
        Assert.NotEqual(first.EffectId(context, "reserve"), second.EffectId(context, "audit"));
        Assert.NotEqual(first.EffectId(context, "reserve"), first.EffectId(context, "audit"));
    }

    static ReactionExecutionContext Context()
    {
        var ev = new Triggered();
        ev.AttachMetadata(new DomainEventMetadata(
            Uuid.Parse("11111111-1111-4111-8111-111111111111", CultureInfo.InvariantCulture),
            Uuid.Parse("22222222-2222-4222-8222-222222222222", CultureInfo.InvariantCulture),
            1, DateTimeOffset.UnixEpoch));
        var record = new DomainEventRecord(new EventStreamAddress("orders", "events", "source"), ev, 0,
            new EventCursor("1"));
        return new ReactionExecutionContext(record, RequestActor.System);
    }

    sealed record Triggered : DomainEvent;

    sealed class EffectProbe(string name)
        : Reactor(new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("orders", "events"), name)
    {
        public Uuid EffectId(IReactorContext context, string effectName) => CreateEffectId(context, effectName);
    }
}

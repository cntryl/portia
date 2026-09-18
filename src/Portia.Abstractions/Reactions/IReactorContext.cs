namespace Cntryl.Portia;

/// <summary>Shared execution state and the triggering source event for a reaction.</summary>
public interface IReactorContext : IExecutionContext
{
    /// <summary>Gets the source event and offsets.</summary>
    DomainEventRecord Source { get; }
}

/// <summary>Carries a triggering event and independent system execution authority.</summary>
/// <typeparam name="TEvent">The handled event type.</typeparam>
public interface IReactorContext<out TEvent> : IReactorContext
    where TEvent : DomainEvent
{
    /// <summary>
    ///     Gets the triggering event, typed. This is the same instance as
    ///     <see cref="IReactorContext.Source" />'s <see cref="DomainEventRecord.Event" />; read this
    ///     one for the event itself and <see cref="IReactorContext.Source" /> for its stream offsets.
    /// </summary>
    /// <remarks>
    ///     Named for its role rather than called <c>Event</c>, which CA1716 reserves on an
    ///     interface member because other .NET languages treat it as a keyword.
    /// </remarks>
    TEvent Trigger { get; }
}

namespace Cntryl.Portia;

/// <summary>
/// Carries the triggering event into a reactor's event handler.
/// </summary>
/// <typeparam name="TEvent">The concrete event type handled.</typeparam>
public interface IReactorContext<out TEvent>
{
    /// <summary>
    /// Gets the event that triggered the reactor.
    /// </summary>
    TEvent Ev { get; }
}

/// <summary>
/// The default <see cref="IReactorContext{TEvent}" /> implementation, constructed by generated
/// reactor dispatch code.
/// </summary>
/// <typeparam name="TEvent">The concrete event type handled.</typeparam>
/// <param name="ev">The event that triggered the reactor.</param>
public sealed class ReactorContext<TEvent>(TEvent ev) : IReactorContext<TEvent>
{
    /// <inheritdoc />
    public TEvent Ev { get; } = ev;
}

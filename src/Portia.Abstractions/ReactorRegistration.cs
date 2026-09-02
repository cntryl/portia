namespace Cntryl.Portia;

/// <summary>
/// Describes one reactor discovered at compile time, so a runner can enumerate every reactor
/// without runtime assembly scanning or reflection.
/// </summary>
/// <param name="reactorType">The concrete reactor type.</param>
/// <param name="resolve">Resolves an instance of the reactor from a service provider.</param>
public sealed class ReactorRegistration(Type reactorType, Func<IServiceProvider, Reactor> resolve)
{
    /// <summary>
    /// Gets the concrete reactor type.
    /// </summary>
    public Type ReactorType { get; } = reactorType ?? throw new ArgumentNullException(nameof(reactorType));

    /// <summary>
    /// Gets the function that resolves an instance of the reactor from a service provider.
    /// </summary>
    public Func<IServiceProvider, Reactor> Resolve { get; } = resolve ?? throw new ArgumentNullException(nameof(resolve));
}

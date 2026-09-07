namespace Cntryl.Portia;

/// <summary>
/// Describes how one registered reactor resolves, so a runner can enumerate every reactor
/// without runtime assembly scanning or reflection. Constructing one has no container side
/// effects — to register and run a reactor, call <c>PortiaBuilder.AddReactor</c>, which builds
/// this descriptor and the workload declaration that drives it.
/// </summary>
/// <param name="reactorType">The concrete reactor type.</param>
/// <param name="resolve">Resolves an instance of the reactor from a service provider.</param>
public sealed class ReactorRegistration(Type reactorType, Func<IServiceProvider, BaseReactor> resolve)
{
    /// <summary>
    /// Gets the concrete reactor type.
    /// </summary>
    public Type ReactorType { get; } = reactorType ?? throw new ArgumentNullException(nameof(reactorType));

    /// <summary>
    /// Gets the function that resolves an instance of the reactor from a service provider.
    /// </summary>
    public Func<IServiceProvider, BaseReactor> Resolve { get; } = resolve ?? throw new ArgumentNullException(nameof(resolve));
}

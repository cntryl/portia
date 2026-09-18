namespace Cntryl.Portia;

/// <summary>
///     A projection store built to serve exactly one projector, named when the store is constructed.
///     Hosting checks it against each projector's registration when the host starts, so a store built
///     for another projector fails there instead of on its first checkpoint load.
/// </summary>
interface IProjectorBoundStore
{
    /// <summary>Throws unless this store serves the projector registered under <paramref name="componentName" />.</summary>
    /// <param name="componentName">The workload name the projector is registered under.</param>
    /// <exception cref="InvalidOperationException">The store serves a different projector.</exception>
    void EnsureServes(string componentName);
}

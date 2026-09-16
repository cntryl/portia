namespace Cntryl.Portia;

/// <summary>Configures fail-closed request authorization at the composition root.</summary>
public sealed class PortiaAuthorizationOptions
{
    readonly HashSet<Type> _anonymous;

    internal PortiaAuthorizationOptions(HashSet<Type> anonymous) => _anonymous = anonymous;

    /// <summary>
    ///     Allows a request, or every request in a request family, to dispatch without an applicable
    ///     authorizer or <see cref="RequiresPermissionAttribute" />.
    /// </summary>
    /// <typeparam name="TRequest">A concrete request type or a request-family interface.</typeparam>
    /// <returns>These options, for chaining.</returns>
    public PortiaAuthorizationOptions AllowAnonymous<TRequest>() where TRequest : IRequestBase
    {
        _ = _anonymous.Add(typeof(TRequest));
        return this;
    }
}

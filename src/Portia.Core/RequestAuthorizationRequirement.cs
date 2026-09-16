namespace Cntryl.Portia;

/// <summary>
///     Fail-closed authorization: every registered request needs an applicable authorizer or a declared
///     permission unless the composition root explicitly allowed it, or its request family, anonymous.
/// </summary>
/// <param name="anonymousRequests">Request types or request-family interfaces allowed without authorization.</param>
sealed class RequestAuthorizationRequirement(IEnumerable<Type> anonymousRequests)
{
    readonly Type[] _anonymous = [.. anonymousRequests.Distinct()];

    // Evaluated once per registered handler while the registry composes, never per dispatch.
    internal bool AllowsAnonymous(Type requestType) =>
        _anonymous.Any(anonymous => anonymous.IsAssignableFrom(requestType));
}

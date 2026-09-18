namespace Cntryl.Portia;

/// <summary>
///     Declares the coarse-grained permission a request requires, evaluated automatically by the
///     generated request bus against <see cref="IPermissionEvaluator" /> before the request's
///     handler runs — applies uniformly across every transport (RPC, queue, notice, schedule,
///     direct in-process send, HTTP), since the check happens once at the bus, not per-transport.
///     For row-level authorization (checks that depend on the request's own data), pair this with
///     <see cref="IRequestAuthorizer{TRequest}" />.
/// </summary>
/// <param name="permission">The permission required, e.g. <c>"users:create"</c>.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class RequiresPermissionAttribute(string permission) : Attribute
{
    /// <summary>
    ///     Gets the permission required.
    /// </summary>
    public string Permission { get; } = permission ?? throw new ArgumentNullException(nameof(permission));
}

namespace Cntryl.Portia;

/// <summary>
///     The kind of change reported by <see cref="TenantLifecycleChange" />.
/// </summary>
public enum TenantLifecycleChangeKind
{
    /// <summary>
    ///     The tenant became active.
    /// </summary>
    Added,

    /// <summary>
    ///     The tenant stopped being active.
    /// </summary>
    Removed
}

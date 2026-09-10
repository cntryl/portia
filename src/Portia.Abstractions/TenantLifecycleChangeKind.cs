namespace Cntryl.Portia;

/// <summary>
///     Whether a tenant became active or stopped being active.
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

namespace Cntryl.Portia;

/// <summary>
///     One tenant lifecycle change reported by <see cref="ITenantDirectory.WatchAsync" />.
/// </summary>
/// <param name="Kind">Whether the tenant became active or stopped being active.</param>
/// <param name="TenantId">The tenant the change applies to.</param>
public readonly record struct TenantLifecycleChange(TenantLifecycleChangeKind Kind, TenantId TenantId);

namespace Cntryl.Portia;

/// <summary>A complete tenant roster at a durable lifecycle stream position.</summary>
public sealed class TenantDirectorySnapshot(EventCursor cursor, IReadOnlyList<TenantId> activeTenants)
{
    /// <summary>The first event after this cursor has not yet been applied to the roster.</summary>
    public EventCursor Cursor { get; } = cursor;

    /// <summary>The tenants active through this cursor.</summary>
    public IReadOnlyList<TenantId> ActiveTenants { get; } = activeTenants ?? throw new ArgumentNullException(nameof(activeTenants));
}

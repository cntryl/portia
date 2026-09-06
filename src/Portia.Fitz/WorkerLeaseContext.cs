using Cntryl.Fitz.Abstractions.Domains.Lease;

namespace Cntryl.Portia;

/// <summary>Provides the held component lease to scoped projection targets and reactor dependencies.</summary>
public sealed class WorkerLeaseContext
{
    /// <summary>Gets the explicit lease route for this component pass.</summary>
    public string Route { get; internal set; } = "";

    /// <summary>Gets the admission authority; durable writes must enforce its fencing token.</summary>
    public LeaseAuthority Authority { get; internal set; }
}

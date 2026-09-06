using Cntryl.Fitz.Abstractions.Domains.Lease;

namespace Cntryl.Portia;

/// <summary>Owns renewable worker membership and its authoritative inventory observer.</summary>
public interface IFleetMembership
{
    /// <summary>Runs the callback with an owned observer while membership is held; cancels and observes it before returning.</summary>
    Task RunAsync(FleetRunOptions options, Func<ILeaseInventoryObserver, CancellationToken, Task> callback, CancellationToken ct = default);
}

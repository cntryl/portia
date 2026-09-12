
namespace Cntryl.Portia;

/// <summary>Owns renewable worker membership and its authoritative inventory observer.</summary>
public interface IFleetMembership
{
    /// <summary>Runs the callback with an owned observer while membership is held; cancels and observes it before returning.</summary>
    /// <param name="options">The membership selector and its lease and timeout settings.</param>
    /// <param name="callback">Runs for as long as membership is held, observing the fleet inventory.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the membership-scoped run.</returns>
    Task RunAsync(FleetRunOptions options, Func<ILeaseInventoryObserver, CancellationToken, Task> callback,
        CancellationToken ct = default);
}

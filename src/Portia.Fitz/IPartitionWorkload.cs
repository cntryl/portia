namespace Cntryl.Portia;

/// <summary>
/// Runs one partition-scoped component while this process holds the partition's lease. The
/// hosting adapter resolves a fresh instance from a fresh dependency-injection scope for each
/// held lease and disposes that scope when the lease run stops.
/// </summary>
public interface IPartitionWorkload
{
    /// <summary>
    /// Runs the component while this process owns <paramref name="partition" />.
    /// </summary>
    /// <param name="partition">The acquired partition route.</param>
    /// <param name="ct">Cancelled when the lease is lost or the host shuts down.</param>
    /// <returns>A task representing the lease-scoped run.</returns>
    Task RunAsync(string partition, CancellationToken ct);
}

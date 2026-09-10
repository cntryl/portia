using System.Collections.Concurrent;

namespace Cntryl.Portia;

sealed class HostingWorkloadState
{
    public ConcurrentQueue<TenantId> StartedTenants { get; } = [];

    public ConcurrentQueue<TenantId> StoppedTenants { get; } = [];

    public ConcurrentQueue<string> StartedPartitions { get; } = [];

    public ConcurrentQueue<string> StoppedPartitions { get; } = [];

    public ConcurrentQueue<Guid> WorkloadInstances { get; } = [];
}

using System.Collections.Concurrent;

namespace Cntryl.Portia;

sealed class ProcessorRuntimeStatusRegistry
{
    readonly ConcurrentDictionary<WorkloadIdentity, ProcessorRuntimeStatus> _owners = new();

    internal ProcessorRuntimeStatus Start(
        WorkloadIdentity identity, int attemptLimit, TimeProvider clock, CancellationToken ownership)
    {
        var observed = new ProcessorRuntimeStatus(attemptLimit, clock, ownership);
        _owners[identity] = observed;
        return observed;
    }

    internal void End(WorkloadIdentity identity, ProcessorRuntimeStatus status)
        => _ = _owners.TryRemove(new KeyValuePair<WorkloadIdentity, ProcessorRuntimeStatus>(identity, status));

    internal ProcessorExecutionStatus? Observe(WorkloadIdentity identity)
        => _owners.TryGetValue(identity, out var status) ? status.Observe() : null;
}

sealed class ProcessorRuntimeStatus(int attemptLimit, TimeProvider clock, CancellationToken ownership)
{
    ProcessorExecutionStatus _status = new("starting", clock.GetUtcNow(), null, null, "start", null, null, attemptLimit);

    internal void Set(string phase, string? stage = null, string? reason = null, string? next = null,
        DateTimeOffset? retryAt = null, int? failures = null)
        => Volatile.Write(ref _status,
            new ProcessorExecutionStatus(phase, clock.GetUtcNow(), stage, reason, next, retryAt, failures, attemptLimit));

    internal ProcessorExecutionStatus? Observe()
        => ownership.IsCancellationRequested ? null : Volatile.Read(ref _status);
}

namespace Cntryl.Portia;

interface IWorkloadDescriptor
{
    Type ComponentType { get; }
    void Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName);
    EventStreamPattern Pattern(IServiceProvider services);
    ValueTask RunPass(IServiceProvider services, ProjectionRunOptions options, CancellationToken ct);
}

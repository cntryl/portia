using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Binds a component to the workload its scope belongs to. Hosting binds explicitly through
///     <c>IWorkloadDescriptor.Bind</c> before reading a pattern; this covers the public
///     <c>RunPass</c> entry points an application can drive itself, so both descriptor kinds behave
///     the same way when called directly. Re-binding the same identity is a no-op.
/// </summary>
static class WorkloadBinding
{
    public static void Apply(IServiceProvider services, Action<WorkloadIdentity, string?> bind)
    {
        if (services.GetService<WorkloadContext>() is { IsInitialized: true } workload)
        {
            bind(workload.Identity, workload.ComponentName);
        }
    }
}

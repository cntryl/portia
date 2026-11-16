using System.Reflection;

namespace Cntryl.Portia;

// A lease client that must not be reached: the behavior under test is decided before any broker call.
// DispatchProxy derives the proxy from this type, so it cannot be sealed.
#pragma warning disable CA1852
class UnusedLeaseClient : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new NotSupportedException($"Unexpected lease call '{targetMethod?.Name}'.");
}

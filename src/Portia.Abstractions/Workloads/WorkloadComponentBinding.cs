namespace Cntryl.Portia;

// The one rule for binding a projector or reactor to its running workload: a component binds once, binding
// it again to the same workload changes nothing, and binding it to another workload or tenant is refused
// rather than silently repointing the checkpoint it has already been writing.
static class WorkloadComponentBinding
{
    // Returns whether this call established the binding, in which case the caller applies the name and tenant.
    internal static bool TryBind(
        ref WorkloadIdentity? bound,
        WorkloadIdentity identity,
        string? componentName,
        string kind,
        Type componentType
    )
    {
        if (componentName is not null)
            _ = IdentityText.Require(componentName, nameof(componentName));

        if (bound is { } existing && existing != identity)
        {
            throw new InvalidOperationException(
                $"{kind} '{componentType.FullName}' is already bound to workload '{existing.Name}' and cannot be rebound to workload '{identity.Name}' or another tenant.");
        }


        if (bound == identity)
            return false;

        bound = identity;
        return true;
    }
}

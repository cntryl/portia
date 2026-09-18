namespace Cntryl.Portia.Testing;

sealed record LifecycleEntry(Type Component, LifecyclePhase Phase)
{
    public override string ToString() => Phase switch
    {
        LifecyclePhase.Permission => "permission",
        _ => $"{Phase.ToString().ToLowerInvariant()} {Component.Name}"
    };
}

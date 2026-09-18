namespace Cntryl.Portia;

sealed class ProjectorContext(CheckpointIdentity identity) : IProjectorContext
{
    public CheckpointIdentity Identity { get; } = identity;
    public bool IsRebuild => Identity.RebuildId is not null;
}

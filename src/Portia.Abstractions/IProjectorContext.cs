namespace Cntryl.Portia;

/// <summary>Projection execution metadata. Application dependencies are injected through constructors.</summary>
public interface IProjectorContext
{
    /// <summary>Gets the component, stream pattern, and optional rebuild generation.</summary>
    CheckpointIdentity Identity { get; }

    /// <summary>Gets whether this execution is building a separate projection generation.</summary>
    bool IsRebuild { get; }
}

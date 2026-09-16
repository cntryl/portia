namespace Cntryl.Portia;

/// <summary>Whether an aggregate operation's pending records are committed or discarded.</summary>
public enum AggregateDisposition
{
    /// <summary>Atomically persist every record the operation produced; nothing pending is a no-op.</summary>
    Commit = 1,

    /// <summary>Persist nothing the operation produced.</summary>
    Discard = 2
}

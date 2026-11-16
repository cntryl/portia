namespace Cntryl.Portia;

/// <summary>The kinds of processor the operational journal and topology describe.</summary>
/// <remarks>These are the exact strings stored in the journal and returned to operators.</remarks>
public static class ProcessorKinds
{
    /// <summary>A projector, whose writes commit with its checkpoint.</summary>
    public const string Projector = "projector";

    /// <summary>A reactor, whose effects are not transactional with its checkpoint.</summary>
    public const string Reactor = "reactor";
}

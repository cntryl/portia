using System.Data.Common;

namespace Cntryl.Portia;

/// <summary>Identifies the processor operation that failed.</summary>
public enum ProcessorFailureStage
{
    /// <summary>Reading source events failed.</summary>
    Reading,
    /// <summary>Opening the projection transaction failed.</summary>
    Beginning,
    /// <summary>An event handler failed.</summary>
    Handling,
    /// <summary>Committing model data or checkpoint progress failed; the outcome may be uncertain.</summary>
    Committing,
    /// <summary>Releasing the projection transaction failed.</summary>
    Disposing,
    /// <summary>Loading authoritative checkpoint progress failed.</summary>
    Loading
}

/// <summary>
///     Describes a failed processor pass without losing its source, resume position, or original exception.
///     Unknown exceptions are eligible for bounded retries; declared failures use their transient flag.
/// </summary>
public sealed class ProcessorFailureException : Exception
{
    /// <summary>Creates the diagnostic for a failed processor operation.</summary>
    /// <param name="identity">The affected projection or reaction.</param>
    /// <param name="checkpoint">The last checkpoint confirmed by this pass, or null when loading it failed.</param>
    /// <param name="stage">The operation that failed.</param>
    /// <param name="firstSource">The first source record of the failed batch, when available.</param>
    /// <param name="lastSource">The last source record of the failed batch, when available.</param>
    /// <param name="innerException">The original exception, when handling threw.</param>
    /// <param name="error">The declared failure, when handling returned one.</param>
    public ProcessorFailureException(CheckpointIdentity identity, ProjectionCheckpoint? checkpoint,
        ProcessorFailureStage stage, DomainEventRecord? firstSource = null, DomainEventRecord? lastSource = null,
        Exception? innerException = null, RequestError? error = null)
        : base($"Processor '{identity?.ComponentName}' failed during {stage}.", innerException)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
        Checkpoint = checkpoint;
        Stage = stage;
        FirstSource = firstSource;
        LastSource = lastSource;
        Error = error ?? innerException switch
        {
            ReactionCommandFailedException reaction => reaction.Error,
            RequestGuardException guard => guard.Error,
            RequestAuthorizationException authorization => authorization.Error,
            _ => null
        };
        CleanupException = innerException?.Data["Portia.CleanupException"] as Exception;
    }

    /// <summary>Gets the affected component, scope, and generation.</summary>
    public CheckpointIdentity Identity { get; }

    /// <summary>Gets confirmed progress, or null when loading it failed; reload authoritative progress before retrying.</summary>
    public ProjectionCheckpoint? Checkpoint { get; }

    /// <summary>Gets the operation that failed.</summary>
    public ProcessorFailureStage Stage { get; }

    /// <summary>Gets the first source record of the failed batch, when available.</summary>
    public DomainEventRecord? FirstSource { get; }

    /// <summary>Gets the last source record of the failed batch, when available.</summary>
    public DomainEventRecord? LastSource { get; }

    /// <summary>Gets the declared application failure, when available.</summary>
    public RequestError? Error { get; }

    /// <summary>Gets whether the hosted worker may retry this failure within its configured attempt budget.</summary>
    public bool IsTransient => Error?.IsTransient ?? (InnerException switch
    {
        DbException database => database.IsTransient,
        ArgumentException or NotSupportedException or ObjectDisposedException => false,
        _ => true
    });

    /// <summary>Gets a secondary cleanup failure without replacing the original failure.</summary>
    public Exception? CleanupException { get; private set; }

    internal static void RecordCleanup(Exception failure, Exception cleanup)
    {
        if (failure is ProcessorFailureException diagnostic)
            diagnostic.CleanupException = diagnostic.CleanupException is { } previous
                ? new AggregateException(previous, cleanup) : cleanup;
        else
            failure.Data["Portia.CleanupException"] = failure.Data["Portia.CleanupException"] is Exception previous
                ? new AggregateException(previous, cleanup) : cleanup;
    }
}

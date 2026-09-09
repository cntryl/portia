namespace Cntryl.Portia;

/// <summary>Describes a retryable queued failure that reached the application terminal threshold.</summary>
public sealed record QueuedRequestFailureContext(
    IRequest? Request,
    RequestMetadata? Metadata,
    RequestInvocation Invocation,
    uint Attempt,
    RequestError? Error,
    Exception? Exception);

/// <summary>Handles a queued request before Portia acknowledges a terminal delivery.</summary>
public interface IQueuedRequestTerminalHandler
{
    /// <summary>Handles the terminal failure. Returning successfully permits acknowledgment.</summary>
    ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default);
}

/// <summary>Controls application-owned queue terminal disposition.</summary>
public sealed class QueueRunnerOptions
{
    /// <summary>Gets or sets the transport-reported attempt at which retryable failures become terminal.</summary>
    public uint? TerminalAttempt { get; set; }
}

namespace Cntryl.Portia;

/// <summary>Describes a retryable queued failure that reached the application terminal threshold.</summary>
public sealed record QueuedRequestFailureContext(
    IRequest? Request,
    RequestMetadata? Metadata,
    RequestInvocation Invocation,
    uint Attempt,
    RequestError? Error,
    Exception? Exception);

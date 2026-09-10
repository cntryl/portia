namespace Cntryl.Portia;

/// <summary>Describes a retryable queued failure that reached the application terminal threshold.</summary>
/// <param name="Request">
///     The failed request, or <see langword="null" /> when the delivery could not
///     be deserialized into one.
/// </param>
/// <param name="Metadata">
///     The logical request identity, or <see langword="null" /> when the delivery
///     carried none that could be read.
/// </param>
/// <param name="Invocation">The concrete ingress facts of the final delivery.</param>
/// <param name="Attempt">The transport-reported attempt on which the request became terminal.</param>
/// <param name="Error">
///     The expected failure the handler reported, or <see langword="null" /> when
///     the delivery ended in an unexpected exception.
/// </param>
/// <param name="Exception">
///     The unexpected exception that ended the delivery, or
///     <see langword="null" /> when the handler reported <paramref name="Error" /> instead.
/// </param>
public sealed record QueuedRequestFailureContext(
    IRequest? Request,
    RequestMetadata? Metadata,
    RequestInvocation Invocation,
    uint Attempt,
    RequestError? Error,
    Exception? Exception);

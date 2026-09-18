namespace Cntryl.Portia;

/// <summary>
///     Describes a queued delivery that became terminal because it reached the retry threshold or
///     because actor validation or request handling failed permanently.
/// </summary>
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
    Exception? Exception)
{
    /// <summary>Gets why the delivery became terminal.</summary>
    /// <remarks>
    ///     The default preserves the meaning of contexts constructed by applications compiled
    ///     against versions that predate this property.
    /// </remarks>
    public QueuedRequestTerminalReason Reason { get; init; }
}

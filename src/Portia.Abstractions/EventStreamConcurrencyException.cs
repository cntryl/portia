namespace Cntryl.Portia;

/// <summary>
/// Thrown by <see cref="IDomainEventWriter.AppendAsync" /> when a stream is not at the expected
/// physical position. Raised-event saves use the aggregate's committed event-stream position;
/// audits use independent session streams. Reloading and re-evaluating a command after a
/// conflict is an application decision; Portia never automatically reruns business commands.
/// </summary>
/// <param name="message">A description of the conflict.</param>
/// <param name="innerException">
/// The underlying store's own exception, if this was translated from one.
/// </param>
public sealed class EventStreamConcurrencyException(string message, Exception? innerException = null)
    : Exception(message, innerException);

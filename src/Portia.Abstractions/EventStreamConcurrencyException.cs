namespace Cntryl.Portia;

/// <summary>
/// Thrown by <see cref="IDomainEventWriter.AppendAsync" /> when a stream is not at the expected
/// version — someone else committed to it first. This is the one exception type every
/// <see cref="IEventStore" /> implementation throws for that specific case, so application code
/// that retries on conflict (reload the aggregate, reapply the command) has a stable contract to
/// catch regardless of which store backs it — a real, distinguishable signal instead of a
/// generic, implementation-specific failure a caller can only tell apart by message text.
/// </summary>
/// <param name="message">A description of the conflict.</param>
/// <param name="innerException">
/// The underlying store's own exception, if this was translated from one.
/// </param>
public sealed class EventStreamConcurrencyException(string message, Exception? innerException = null)
    : Exception(message, innerException);

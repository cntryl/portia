namespace Cntryl.Portia;

/// <summary>
/// Thrown when a Fitz KV checkpoint or projection-batch commit conflicts with a concurrent
/// writer to the same key (Fitz's <c>KvIsolationConflict</c> domain error). Mirrors
/// <see cref="EventStreamConcurrencyException"/>'s role for the event store: one stable type to
/// catch. The next attempt must reload authoritative progress before applying events again;
/// Portia never automatically reruns the work that produced it.
/// </summary>
/// <param name="message">A description of the conflict.</param>
/// <param name="innerException">The underlying Fitz KV exception this was translated from.</param>
public sealed class FitzKvConcurrencyException(string message, Exception? innerException = null)
    : Exception(message, innerException);

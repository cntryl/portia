namespace Cntryl.Portia;

/// <summary>The event store could not establish the outcome of a requested commit.</summary>
/// <param name="message">A description of the unconfirmed commit.</param>
/// <param name="innerException">The failure that prevented the store from confirming the commit.</param>
public sealed class EventStreamCommitUnknownException(string message, Exception innerException)
    : Exception(message, innerException);

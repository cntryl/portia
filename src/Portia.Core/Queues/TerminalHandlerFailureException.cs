namespace Cntryl.Portia;

/// <summary>
///     Thrown when a queued request's terminal-failure callback fails. The delivery remains
///     unacknowledged so ownership stays with the queue transport, and the runner faults so the
///     application failure is visible to its host.
/// </summary>
/// <param name="innerException">The exception thrown by the terminal handler.</param>
public sealed class TerminalHandlerFailureException(Exception innerException)
    : Exception("The queued request terminal handler failed.", innerException);

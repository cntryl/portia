namespace Cntryl.Portia;

/// <summary>
///     Thrown by an <see cref="IProjectionStore" /> when committing a batch conflicts with a
///     concurrent writer. The caller must reload authoritative projection progress before deciding
///     whether to apply the events again. Hosted workers reload that progress in a fresh scope before
///     retrying a failed pass within their configured budget; stores never retry application writes internally.
/// </summary>
/// <param name="message">A description of the conflict.</param>
/// <param name="innerException">The backing store's own exception, if this was translated from one.</param>
public class ProjectionConcurrencyException(string message, Exception? innerException = null)
    : Exception(message, innerException);

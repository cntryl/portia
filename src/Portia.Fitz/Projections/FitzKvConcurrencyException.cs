namespace Cntryl.Portia;

/// <summary>
///     Legacy Fitz-specific subtype retained so existing catch clauses remain source- and
///     binary-compatible. New code should catch <see cref="ProjectionConcurrencyException" /> so
///     one handler works with every projection-store adapter. The next attempt must reload
///     authoritative progress before applying events again; Portia never automatically reruns the
///     work that produced it.
/// </summary>
/// <param name="message">A description of the conflict.</param>
/// <param name="innerException">The underlying Fitz KV exception this was translated from.</param>
[Obsolete(
    $"Catch {nameof(ProjectionConcurrencyException)} so the same handler works with every projection-store adapter.")]
public sealed class FitzKvConcurrencyException(string message, Exception? innerException = null)
    : ProjectionConcurrencyException(message, innerException);

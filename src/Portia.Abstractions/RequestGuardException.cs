namespace Cntryl.Portia;

/// <summary>
///     Thrown when a request guard (<see cref="IRequestGuard{TRequest}" />) fails for a streamed request
///     (<see cref="IStreamRequest{TOut}" />). A stream has no single <see cref="Result" /> to carry the
///     failure, so the stream ends with this exception before producing its first item, exactly as a
///     failed authorization ends it with <see cref="RequestAuthorizationException" />.
/// </summary>
/// <param name="error">The guard failure.</param>
public sealed class RequestGuardException(RequestError error) : Exception(error?.Message)
{
    /// <summary>Gets the guard failure.</summary>
    public RequestError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}

namespace Cntryl.Portia;

/// <summary>
/// Thrown when a streamed request (<see cref="IStreamRequest{TOut}" />) fails its declared
/// authorization (<see cref="RequiresPermissionAttribute" /> and/or
/// <see cref="IRequestAuthorizer{TRequest}" />). A stream has no single outcome to wrap in a
/// <see cref="Result" /> the way <see cref="IRequestBus.SendAsync(IRequest, System.Security.Claims.ClaimsPrincipal, CancellationToken)" />
/// does, so a failed authorization simply ends the stream with this exception instead — same as
/// any other failure in an <see cref="IAsyncEnumerable{T}" /> producer.
/// </summary>
/// <param name="error">The authorization failure.</param>
public sealed class RequestAuthorizationException(RequestError error) : Exception(error?.Message)
{
    /// <summary>
    /// Gets the authorization failure.
    /// </summary>
    public RequestError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}

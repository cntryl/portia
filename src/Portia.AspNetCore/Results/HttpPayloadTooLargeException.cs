using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

/// <summary>Signals that a generated endpoint's bounded JSON body exceeded its configured limit.</summary>
public sealed class HttpPayloadTooLargeException : BadHttpRequestException
{
    /// <summary>Creates the boundary exception.</summary>
    public HttpPayloadTooLargeException() : base("The request body is too large.", StatusCodes.Status413PayloadTooLarge)
    {
    }
}

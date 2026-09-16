using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

/// <summary>Signals that a generated endpoint refused a request body's content type.</summary>
public sealed class HttpUnsupportedMediaTypeException : BadHttpRequestException
{
    /// <summary>Creates the boundary exception.</summary>
    /// <param name="message">The non-sensitive detail reported to the caller.</param>
    public HttpUnsupportedMediaTypeException(string message)
        : base(message, StatusCodes.Status415UnsupportedMediaType)
    {
    }
}

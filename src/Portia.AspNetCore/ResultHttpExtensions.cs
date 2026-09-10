using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

/// <summary>
/// Maps <see cref="Result" />/<see cref="Result{T}" /> outcomes onto HTTP responses, using the
/// same <see cref="RequestErrorKind" /> every transport already branches on.
/// </summary>
public static class ResultHttpExtensions
{
    /// <summary>Identifies the response header that preserves <see cref="RequestError.IsTransient" />.</summary>
    public const string TransientHeaderName = "Portia-Transient";

    /// <summary>
    /// Maps a no-result outcome onto an HTTP response: 204 on success, or a status matching the
    /// error's <see cref="RequestErrorKind" />.
    /// </summary>
    /// <param name="result">The outcome to map.</param>
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? Results.NoContent() : ToProblem(result.Error);

    /// <summary>
    /// Maps an outcome with a result value onto an HTTP response: 200 with the value on
    /// success, or a status matching the error's <see cref="RequestErrorKind" />.
    /// </summary>
    /// <typeparam name="T">The type of the value produced on success.</typeparam>
    /// <param name="result">The outcome to map.</param>
    /// <param name="typeInfo">The application-owned source-generated metadata for the result value.</param>
    public static IResult ToHttpResult<T>(this Result<T> result, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return result.IsSuccess ? Results.Json(result.Value, typeInfo) : ToProblem(result.Error);
    }

    static RequestErrorResult ToProblem(RequestError error) => new(error);

    sealed class RequestErrorResult(RequestError error) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.StatusCode = error.Kind switch
            {
                RequestErrorKind.Validation => StatusCodes.Status400BadRequest,
                RequestErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
                RequestErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                RequestErrorKind.NotFound => StatusCodes.Status404NotFound,
                RequestErrorKind.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status500InternalServerError,
            };
            httpContext.Response.Headers[TransientHeaderName] = error.IsTransient ? "true" : "false";
            if (error.Kind == RequestErrorKind.Unauthorized)
            {
                httpContext.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await PortiaProblemDetails.WriteAsync(httpContext, httpContext.Response.StatusCode, error.Message, error.IsTransient)
                .ConfigureAwait(false);
        }
    }
}

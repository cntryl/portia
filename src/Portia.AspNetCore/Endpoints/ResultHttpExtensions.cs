using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

/// <summary>
///     Maps <see cref="Result" />/<see cref="Result{T}" /> outcomes onto HTTP responses, using the
///     same <see cref="RequestErrorKind" /> every transport already branches on.
/// </summary>
public static class ResultHttpExtensions
{
    /// <summary>Identifies the response header that preserves <see cref="RequestError.IsTransient" />.</summary>
    public const string TransientHeaderName = "Portia-Transient";

    /// <summary>
    ///     Maps a no-result outcome onto an HTTP response: 204 on success, or a status matching the
    ///     error's <see cref="RequestErrorKind" />.
    /// </summary>
    /// <param name="result">The outcome to map.</param>
    /// <returns>A 204 result on success, or a problem-details result describing the error.</returns>
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? Results.NoContent() : ToProblem(result.Error);

    /// <summary>
    ///     Maps an outcome with a result value onto an HTTP response: 200 with the value on
    ///     success, or a status matching the error's <see cref="RequestErrorKind" />.
    /// </summary>
    /// <typeparam name="T">The type of the value produced on success.</typeparam>
    /// <param name="result">The outcome to map.</param>
    /// <param name="typeInfo">The application-owned source-generated metadata for the result value.</param>
    /// <returns>A 200 result carrying the value, or a problem-details result describing the error.</returns>
    public static IResult ToHttpResult<T>(this Result<T> result, JsonTypeInfo<T> typeInfo) =>
        ToHttpResult(result, typeInfo, null);

    /// <summary>Maps a result to HTTP, applying configured create status and location metadata from the current endpoint.</summary>
    /// <typeparam name="T">The result payload type.</typeparam>
    /// <param name="result">The handler outcome.</param>
    /// <param name="typeInfo">The application-owned JSON metadata for the result type.</param>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The HTTP response for the result.</returns>
    public static IResult ToHttpResult<T>(this Result<T> result, JsonTypeInfo<T> typeInfo, HttpContext? context)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (!result.IsSuccess)
            return ToProblem(result.Error);
        if (result.Value is IPortiaCreateOutcome create && context?.GetEndpoint()?.Metadata.GetMetadata<PortiaCreatedLocation>() is { } location)
            return new CreatedOutcomeResult(create, typeInfo.Options, location.Resolve(context));
        return Results.Json(result.Value, typeInfo);
    }

    sealed class CreatedOutcomeResult(IPortiaCreateOutcome outcome, JsonSerializerOptions options, string location) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = outcome.IsCreated ? StatusCodes.Status201Created : StatusCodes.Status200OK;
            context.Response.Headers.Location = location;
            context.Response.ContentType = "application/json; charset=utf-8";
            var payloadInfo = outcome.PayloadTypeInfo(options);
            await JsonSerializer.SerializeAsync(context.Response.Body, outcome.UntypedValue, payloadInfo,
                context.RequestAborted).ConfigureAwait(false);
        }
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
                _ => StatusCodes.Status500InternalServerError
            };
            httpContext.Response.Headers[TransientHeaderName] = error.IsTransient ? "true" : "false";
            if (error.Kind == RequestErrorKind.Unauthorized)
            {
                httpContext.Response.Headers.WWWAuthenticate = "Bearer";
            }
            else
            {
                await PortiaProblemDetails.WriteAsync(httpContext, httpContext.Response.StatusCode, error.Message,
                        error.IsTransient)
                    .ConfigureAwait(false);
            }
        }
    }
}

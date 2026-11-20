using System.ComponentModel;
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
    public static IResult ToHttpResult<T>(this Result<T> result, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return result.IsSuccess ? Results.Json(result.Value, typeInfo) : ToProblem(result.Error);
    }

    /// <summary>Maps a generated create endpoint's result using its resolved location.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResult ToCreatedHttpResult<T>(this Result<T> result, JsonSerializerOptions options,
        Func<object?, string> locationResolver, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(locationResolver);
        if (!result.IsSuccess)
            return ToProblem(result.Error);
        if (result.Value is not IPortiaCreateOutcome outcome)
            throw new InvalidOperationException("A successful Created response requires a non-null create outcome.");
        var includePayload = context.GetEndpoint()?.Metadata.GetMetadata<PortiaCreatedLocation>()?.IncludePayload == true;
        var payloadTypeInfo = includePayload ? outcome.PayloadTypeInfo(options) : null;
        var status = outcome.Kind switch
        {
            PortiaCreateOutcomeKind.Created => StatusCodes.Status201Created,
            PortiaCreateOutcomeKind.Repeated => StatusCodes.Status200OK,
            PortiaCreateOutcomeKind.Replaced when HttpMethods.IsPut(context.Request.Method) =>
                payloadTypeInfo is null ? StatusCodes.Status204NoContent : StatusCodes.Status200OK,
            _ => throw new InvalidOperationException("The resource write outcome is not supported by this HTTP method.")
        };
        return new CreatedOutcomeResult(outcome, payloadTypeInfo, locationResolver(outcome.UntypedValue), status);
    }

    sealed class CreatedOutcomeResult(IPortiaCreateOutcome outcome, JsonTypeInfo? payloadTypeInfo, string location, int status)
        : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = location;
            if (payloadTypeInfo is not null)
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(context.Response.Body, outcome.UntypedValue, payloadTypeInfo,
                    context.RequestAborted).ConfigureAwait(false);
            }
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

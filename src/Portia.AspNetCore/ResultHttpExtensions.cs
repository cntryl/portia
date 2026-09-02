using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

/// <summary>
/// Maps <see cref="Result" />/<see cref="Result{T}" /> outcomes onto HTTP responses, using the
/// same <see cref="RequestErrorKind" /> every transport already branches on.
/// </summary>
public static class ResultHttpExtensions
{
    /// <summary>
    /// Maps a no-result outcome onto an HTTP response: 204 on success, or a status matching the
    /// error's <see cref="RequestErrorKind" />.
    /// </summary>
    /// <param name="result">The outcome to map.</param>
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? Results.NoContent() : ToProblem(result.Error!);

    /// <summary>
    /// Maps an outcome with a result value onto an HTTP response: 200 with the value on
    /// success, or a status matching the error's <see cref="RequestErrorKind" />.
    /// </summary>
    /// <typeparam name="T">The type of the value produced on success.</typeparam>
    /// <param name="result">The outcome to map.</param>
    public static IResult ToHttpResult<T>(this Result<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : ToProblem(result.Error!);

    static IResult ToProblem(RequestError error) => error.Kind switch
    {
        RequestErrorKind.Validation => Results.BadRequest(new { error.Message }),
        RequestErrorKind.Unauthorized => Results.Unauthorized(),
        RequestErrorKind.Forbidden => Results.Problem(error.Message, statusCode: StatusCodes.Status403Forbidden),
        RequestErrorKind.NotFound => Results.NotFound(new { error.Message }),
        RequestErrorKind.Conflict => Results.Conflict(new { error.Message }),
        _ => Results.Problem(error.Message, statusCode: StatusCodes.Status500InternalServerError),
    };
}

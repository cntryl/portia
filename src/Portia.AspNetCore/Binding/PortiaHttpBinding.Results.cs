using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

public static partial class PortiaHttpBinding
{
    /// <summary>Creates the asynchronous acceptance receipt.</summary>
    /// <param name="context">The current HTTP request, whose response gains <c>Preference-Applied</c>.</param>
    /// <param name="requestId">The logical identity the caller can track the enqueued request by.</param>
    /// <returns>A 202 Accepted result carrying the request identity.</returns>
    public static IResult Accepted(HttpContext context, Uuid requestId)
    {
        context.Response.Headers["Preference-Applied"] = "respond-async";
        return new AcceptedReceiptResult(requestId,
            GetJsonOptions(context).PropertyNamingPolicy?.ConvertName("RequestId") ?? "RequestId");
    }

    /// <summary>Writes the stable Portia problem contract.</summary>
    /// <param name="statusCode">The HTTP status to respond with.</param>
    /// <param name="message">The non-sensitive detail reported to the caller.</param>
    /// <returns>A problem-details result.</returns>
    public static IResult Problem(int statusCode, string message) => new ProblemResult(statusCode, message);

    /// <summary>Logs an unexpected HTTP failure and returns a non-sensitive response.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <param name="exception">The unexpected failure, recorded through Portia's telemetry contract.</param>
    /// <returns>A 500 problem-details result that discloses nothing about the failure.</returns>
    public static IResult Unexpected(HttpContext context, Exception exception)
    {
        PortiaTelemetry.RecordRunnerFault("Http", RunnerFaultStage.Execution, exception,
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Cntryl.Portia.Http"));
        return Problem(StatusCodes.Status500InternalServerError, "An unexpected error occurred.");
    }

    sealed class AcceptedReceiptResult(Uuid requestId, string propertyName) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            context.Response.ContentType = "application/json; charset=utf-8";
            await using var writer = new Utf8JsonWriter(context.Response.Body);
            writer.WriteStartObject();
            writer.WriteString(propertyName, requestId.ToString());
            writer.WriteEndObject();
            await writer.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
    }

    sealed class ProblemResult(int statusCode, string message) : IResult
    {
        public Task ExecuteAsync(HttpContext context) => PortiaProblemDetails.WriteAsync(context, statusCode, message);
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Cntryl.Portia;

static class PortiaProblemDetails
{
    public static async Task WriteAsync(HttpContext context, int statusCode, string detail, bool? transient = null)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        writer.WriteString("type", "about:blank");
        writer.WriteString("title", ReasonPhrases.GetReasonPhrase(statusCode));
        writer.WriteNumber("status", statusCode);
        writer.WriteString("detail", detail);
        writer.WriteString("instance", context.Request.PathBase.Add(context.Request.Path).Value ?? "/");
        if (transient is { } isTransient)
        {
            writer.WriteBoolean("transient", isTransient);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }
}

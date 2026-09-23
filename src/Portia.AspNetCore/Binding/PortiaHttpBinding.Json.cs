using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

public static partial class PortiaHttpBinding
{
    // Content-Length is the caller's claim, not a measurement. Sizing the buffer from it lets one
    // small request reserve the whole configured maximum, so the hint is capped: an ordinary body
    // still lands in a single allocation, and a dishonest header cannot reserve more than this.
    const int MaximumInitialBodyBytes = 64 * 1024;
    const int BodyChunkBytes = 16 * 1024;
    const string JsonMediaTypeRequired = "Request bodies must declare a JSON content type.";

    static readonly ReadOnlyMemory<byte> EmptyObjectUtf8 = "{}"u8.ToArray();

    /// <summary>Reads one bounded JSON object request body.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <param name="bodyRequired">Whether an empty body is rejected rather than read as <c>{}</c>.</param>
    /// <param name="ct">A token that can cancel the read.</param>
    /// <returns>The parsed body, or an empty object when the body was empty and optional.</returns>
    /// <exception cref="HttpPayloadTooLargeException">The body exceeds <c>PortiaHttpOptions.MaxJsonBodyBytes</c>.</exception>
    /// <exception cref="HttpUnsupportedMediaTypeException">
    ///     The request declares a non-JSON content type, or carries a body without declaring one.
    /// </exception>
    public static async ValueTask<JsonDocument> ReadJsonBodyAsync(HttpContext context, bool bodyRequired,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        // A cross-site page can send a text/plain or untyped body without a CORS preflight, and that
        // body can still be valid JSON, so only a declared JSON media type is read as JSON. A request
        // without a content type is accepted only when it carries no body.
        var declaresJson = context.Request.HasJsonContentType();
        if (!declaresJson && (context.Request.ContentType is not null || context.Request.ContentLength > 0))
            throw new HttpUnsupportedMediaTypeException(JsonMediaTypeRequired);
        var maximum = MaximumBodyBytes(context);

        if (context.Request.ContentLength > 0 && context.Request.ContentLength > maximum)
            throw new HttpPayloadTooLargeException();

        var hint = Math.Min(Math.Min(context.Request.ContentLength ?? 0, maximum), MaximumInitialBodyBytes);
        await using var buffer = new MemoryStream((int)hint);
        var chunk = ArrayPool<byte>.Shared.Rent(BodyChunkBytes);
        try
        {
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maximum)
                {
                    throw new HttpPayloadTooLargeException();
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch (BadHttpRequestException ex) when (IsServerBodyLimit(ex))
        {
            throw new HttpPayloadTooLargeException();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        if (buffer.Length == 0)
        {
            return bodyRequired
                ? throw new BadHttpRequestException("Missing required request body.")
                : JsonDocument.Parse(EmptyObjectUtf8);
        }

        if (!declaresJson)
            throw new HttpUnsupportedMediaTypeException(JsonMediaTypeRequired);

        buffer.Position = 0;
        var json = GetJsonOptions(context);
        return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions
        {
            AllowTrailingCommas = json.AllowTrailingCommas,
            CommentHandling = json.ReadCommentHandling,
            MaxDepth = json.MaxDepth
        }, ct).ConfigureAwait(false);
    }
}

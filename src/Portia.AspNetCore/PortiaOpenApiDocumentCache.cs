using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

/// <summary>
///     Renders the application's OpenAPI document once and serves the rendered bytes.
///     <para>
///         Composing the document is not cheap — it walks every described endpoint, resolves a schema
///         per parameter and response, and runs each transformer — and the result is the same for
///         every caller. Endpoints are fixed once the host starts, so the document is rendered on the
///         first request and reused for the life of the process; later requests only copy bytes.
///     </para>
///     <para>
///         Rendering lazily rather than during startup keeps the cost off the critical path of a host
///         that never serves the document, which matters most where cold start is visible.
///     </para>
/// </summary>
sealed class PortiaOpenApiDocumentCache(IServiceProvider services, string documentName) : IDisposable
{
    const string JsonContentType = "application/json; charset=utf-8";
    const string YamlContentType = "text/plain+yaml; charset=utf-8";

    readonly SemaphoreSlim _gate = new(1, 1);
    RenderedDocument? _rendered;

    public void Dispose() => _gate.Dispose();

    internal async Task WriteAsync(HttpContext context, bool yaml)
    {
        var rendered = await GetAsync(context.RequestAborted).ConfigureAwait(false);
        var payload = yaml ? rendered.Yaml : rendered.Json;
        context.Response.ContentType = yaml ? YamlContentType : JsonContentType;
        context.Response.ContentLength = payload.Length;
        await context.Response.Body.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
    }

    async ValueTask<RenderedDocument> GetAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _rendered) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A second caller that queued behind the render uses its result rather than repeating it.
            if (Volatile.Read(ref _rendered) is { } current)
            {
                return current;
            }

            var document = await services.GetRequiredKeyedService<IOpenApiDocumentProvider>(documentName)
                .GetOpenApiDocumentAsync(ct).ConfigureAwait(false);

            // Assigned only once both renders succeed: a document that cannot be composed — a
            // duplicated operation ID, say — must surface on every request, not be cached as a
            // failure or, worse, half-rendered.
            var rendered = new RenderedDocument(Render(document, false), Render(document, true));
            Volatile.Write(ref _rendered, rendered);
            return rendered;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    static ReadOnlyMemory<byte> Render(OpenApiDocument document, bool yaml)
    {
        using var buffer = new MemoryStream();
        using (var text = new StreamWriter(buffer, new UTF8Encoding(false), leaveOpen: true))
        {
            IOpenApiWriter writer = yaml ? new OpenApiYamlWriter(text) : new OpenApiJsonWriter(text);
            document.SerializeAs(OpenApiSpecVersion.OpenApi3_1, writer);
        }

        return buffer.ToArray();
    }

    sealed record RenderedDocument(ReadOnlyMemory<byte> Json, ReadOnlyMemory<byte> Yaml);
}

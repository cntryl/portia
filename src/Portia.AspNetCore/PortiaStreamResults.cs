using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>HTTP stream results that authorize before starting the response.</summary>
public static class PortiaStreamResults
{
    /// <summary>Writes a sequence as an incrementally flushed JSON array.</summary>
    /// <typeparam name="T">The type of each item produced.</typeparam>
    /// <param name="source">The sequence to write, enumerated as the response is flushed.</param>
    /// <returns>A result that streams the sequence as one JSON array.</returns>
    public static IResult Json<T>(IAsyncEnumerable<T> source) => new StreamResult<T>(source, false);

    /// <summary>Writes a sequence as server-sent event data.</summary>
    /// <typeparam name="T">The type of each item produced.</typeparam>
    /// <param name="source">The sequence to write, enumerated as the response is flushed.</param>
    /// <returns>A result that streams the sequence as <c>text/event-stream</c> data.</returns>
    public static IResult Sse<T>(IAsyncEnumerable<T> source) => new StreamResult<T>(source, true);

    sealed class StreamResult<T>(IAsyncEnumerable<T> source, bool sse) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            try
            {
                await WriteAsync(httpContext).ConfigureAwait(false);
            }
            catch (RequestAuthorizationException ex) when (!httpContext.Response.HasStarted)
            {
                await Result.Failure(ex.Error).ToHttpResult().ExecuteAsync(httpContext).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                httpContext.Abort();
            }
            catch (Exception ex) when (httpContext.Response.HasStarted)
            {
                PortiaTelemetry.RecordRunnerFault("HttpStream", RunnerFaultStage.Execution, ex,
                    httpContext.RequestServices.GetService<ILogger<StreamResult<T>>>());
                httpContext.Abort();
            }
        }

        async Task WriteAsync(HttpContext context)
        {
            var ct = context.RequestAborted;
            var options = PortiaHttpBinding.GetJsonOptions(context);
            var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
            await using var iterator = source.GetAsyncEnumerator(ct);
            var hasItem = await iterator.MoveNextAsync().ConfigureAwait(false);
            var response = context.Response;
            response.ContentType = sse ? "text/event-stream" : "application/json; charset=utf-8";
            if (sse)
            {
                response.Headers.CacheControl = "no-cache, no-store";
            }
            else
            {
                await response.WriteAsync("[", ct).ConfigureAwait(false);
            }

            var first = true;
            while (hasItem)
            {
                if (sse)
                {
                    var data = iterator.Current is string text
                        ? text
                        : JsonSerializer.Serialize(iterator.Current, typeInfo);
                    foreach (var line in data.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                                 .Split('\n'))
                    {
                        await response.WriteAsync("data: " + line + "\n", ct).ConfigureAwait(false);
                    }

                    await response.WriteAsync("\n", ct).ConfigureAwait(false);
                }
                else
                {
                    if (!first)
                    {
                        await response.WriteAsync(",", ct).ConfigureAwait(false);
                    }

                    await JsonSerializer.SerializeAsync(response.Body, iterator.Current, typeInfo, ct)
                        .ConfigureAwait(false);
                }

                first = false;
                await response.Body.FlushAsync(ct).ConfigureAwait(false);
                hasItem = await iterator.MoveNextAsync().ConfigureAwait(false);
            }

            if (!sse)
            {
                await response.WriteAsync("]", ct).ConfigureAwait(false);
            }
        }
    }
}

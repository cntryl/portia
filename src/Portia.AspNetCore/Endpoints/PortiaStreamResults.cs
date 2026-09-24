using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
            catch (RequestGuardException ex) when (!httpContext.Response.HasStarted)
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


        // Waiting for the next item and keeping the connection alive are the same wait: the pending
        // move is held across each comment rather than restarted, since an enumerator cannot be
        // advanced twice concurrently. For the same reason the enumerator cannot be disposed while
        // that move is still running, so a failed comment stops the source and waits for the move
        // to finish before the failure surfaces; the source's own cleanup then runs on disposal.
        static async ValueTask<bool> NextAsync(IAsyncEnumerator<T> iterator, CancellationTokenSource source,
            HttpResponse response, TimeSpan? keepAlive, CancellationToken ct)
        {
            if (keepAlive is not { } interval)
            {
                return await iterator.MoveNextAsync().ConfigureAwait(false);
            }

            var pending = iterator.MoveNextAsync().AsTask();
            try
            {
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var elapsed = Task.Delay(interval, idle.Token);
                    if (await Task.WhenAny(pending, elapsed).ConfigureAwait(false) == pending)
                    {
                        // The losing delay is cancelled rather than left to fire later.
                        await idle.CancelAsync().ConfigureAwait(false);
                        return await pending.ConfigureAwait(false);
                    }

                    await response.WriteAsync(":\n\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                await source.CancelAsync().ConfigureAwait(false);
                // Only the move's completion matters here; its outcome is superseded by the failure.
                await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw;
            }
        }

        // Every line of the payload becomes its own data field, and \r\n, \r and \n all count as
        // breaks. The common payload has no break at all, so that case writes once instead of
        // rewriting the whole string twice and splitting it into an array of lines.
        static async ValueTask WriteEventAsync(HttpResponse response, string data, CancellationToken ct)
        {
            if (data.AsSpan().IndexOfAny('\n', '\r') < 0)
            {
                await response.WriteAsync("data: " + data + "\n\n", ct).ConfigureAwait(false);
                return;
            }

            // Only indices cross the awaits; a span cannot be held across one.
            var start = 0;
            while (start <= data.Length)
            {
                var next = data.AsSpan(start).IndexOfAny('\n', '\r');
                var length = next < 0 ? data.Length - start : next;
                await response.WriteAsync(string.Concat("data: ", data.AsSpan(start, length), "\n"), ct)
                    .ConfigureAwait(false);
                if (next < 0)
                {
                    break;
                }

                // A \r\n pair is one break, not two.
                var breakAt = start + next;
                start = breakAt + (data[breakAt] == '\r' && breakAt + 1 < data.Length && data[breakAt + 1] == '\n'
                    ? 2
                    : 1);
            }

            await response.WriteAsync("\n", ct).ConfigureAwait(false);
        }

        async Task WriteAsync(HttpContext context)
        {
            var ct = context.RequestAborted;
            var options = PortiaHttpBinding.GetJsonOptions(context);
            var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
            // A comment keeps an idle event source's connection from being reclaimed. It applies
            // only to the event-stream shape: a JSON array has nowhere to put one.
            var keepAlive = sse
                ? context.RequestServices.GetService<IOptions<PortiaHttpOptions>>()?.Value.ServerSentEventKeepAlive
                : null;
            // The source gets its own token so a failed keep-alive can stop it without aborting the
            // request; the request's cancellation still reaches it through the link.
            using var sourceLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await using var iterator = source.GetAsyncEnumerator(sourceLifetime.Token);
            var pending = iterator.MoveNextAsync();
            // The first item is awaited before any header is written so authorization can still
            // choose the status code; keep-alive only starts once the response is committed.
            var hasItem = await pending.ConfigureAwait(false);
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
                    // A string item is written as-is: the event-stream body is text, and the
                    // document declares the item's own schema for it. Anything else is JSON.
                    var data = iterator.Current is string text
                        ? text
                        : JsonSerializer.Serialize(iterator.Current, typeInfo);
                    await WriteEventAsync(response, data, ct).ConfigureAwait(false);
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
                hasItem = await NextAsync(iterator, sourceLifetime, response, keepAlive, ct).ConfigureAwait(false);
            }

            if (!sse)
            {
                await response.WriteAsync("]", ct).ConfigureAwait(false);
            }
        }
    }
}

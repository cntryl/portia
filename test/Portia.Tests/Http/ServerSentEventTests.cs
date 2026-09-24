using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Covers the server-sent-event framing a browser <c>EventSource</c> actually needs. A stream
///     that is quiet is the normal case for an event source, and an idle connection is exactly what
///     an intermediary reclaims, so the framing has to say something while nothing is happening.
/// </summary>
public sealed class ServerSentEventTests : IAsyncDisposable
{
    WebApplication? _app;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    ///     A payload's line breaks are the one thing SSE framing cannot pass through: each line has
    ///     to become its own <c>data:</c> field, for all three line-break forms.
    /// </summary>
    [Fact]
    public async Task ShouldFrameEachLineOfAMultiLinePayloadSeparately()
    {
        var client = await StartAsync(TimeSpan.FromSeconds(30));

        using var response = await client.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead);
        var body = await ReadUntilAsync(response, "fourth\n\n");

        Assert.Contains("data: first\ndata: second\ndata: third\ndata: fourth\n\n", body, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An event source that has nothing to say must still say something, or the proxies and
    ///     browsers between it and the caller will reclaim a connection they consider dead.
    /// </summary>
    [Fact]
    public async Task ShouldSendKeepAliveCommentsWhileTheStreamIsIdle()
    {
        var client = await StartAsync(TimeSpan.FromMilliseconds(50));

        using var response = await client.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead);
        var body = await ReadUntilAsync(response, ":\n\n");

        Assert.Contains(":\n\n", body, StringComparison.Ordinal);
    }

    /// <summary>The comment stream is disabled by configuration, for a caller that does not want it.</summary>
    [Fact]
    public async Task ShouldNotSendKeepAliveCommentsWhenDisabled()
    {
        var client = await StartAsync(null);

        using var response = await client.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead);
        var body = await ReadUntilAsync(response, "fourth");
        await Task.Delay(200);

        Assert.DoesNotContain(":\n\n", body, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A keep-alive write that fails while the source is idle stops the source before the failure
    ///     surfaces, so the handler's cleanup runs instead of its iterator being abandoned mid-wait.
    /// </summary>
    [Fact]
    public async Task ShouldStopAnIdleSourceWhenAKeepAliveWriteFails()
    {
        var services = new ServiceCollection()
            .AddSingleton(new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() })
            .Configure<PortiaHttpOptions>(options => options.ServerSentEventKeepAlive = TimeSpan.FromMilliseconds(10));
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new FailingAfterFirstWriteStream();
        var stopped = new StrongBox<bool>();

        _ = await Assert.ThrowsAsync<IOException>(() =>
            PortiaStreamResults.Sse(IdleAfterFirstItem(stopped)).ExecuteAsync(context));

        Assert.True(stopped.Value);
    }

    static async IAsyncEnumerable<string> IdleAfterFirstItem(StrongBox<bool> stopped,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            yield return "first";
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        finally
        {
            stopped.Value = true;
        }
    }

    static async Task<string> ReadUntilAsync(HttpResponseMessage response, string marker)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(stream);
        var buffer = new char[256];
        var text = new StringBuilder();
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), deadline.Token);
            if (read == 0)
                break;
            _ = text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    async Task<HttpClient> StartAsync(TimeSpan? keepAlive)
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _ = builder.Services.Configure<PortiaHttpOptions>(options =>
            options.ServerSentEventKeepAlive = keepAlive);
        _app = builder.Build();
        _ = _app.MapPortiaGetSse<HttpStallingStream, string>("/events");
        await _app.StartAsync();
        var client = _app.GetTestClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    sealed class FailingAfterFirstWriteStream : MemoryStream
    {
        int _writes;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ++_writes > 1
                ? ValueTask.FromException(new IOException("The connection was reset."))
                : base.WriteAsync(buffer, cancellationToken);
    }
}

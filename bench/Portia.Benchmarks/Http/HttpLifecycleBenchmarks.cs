using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>Measures real generated endpoints from body receipt through response consumption.</summary>
[MemoryDiagnoser]
public class HttpLifecycleBenchmarks : IDisposable
{
    HttpLifecycleApplication _application = null!;

    /// <summary>Gets or sets TestServer or actual loopback Kestrel transport.</summary>
    [Params("inprocess", "loopback")]
    public string Transport { get; set; } = "inprocess";

    /// <summary>Gets or sets the prepared payload character count.</summary>
    [Params(256, 4096, 65536)]
    public int PayloadLength { get; set; }

    /// <summary>Gets or sets whether Content-Length is known or streaming.</summary>
    [Params(true, false)]
    public bool KnownLength { get; set; }

    /// <summary>Starts the endpoint, prepares bodies, and verifies failures before handlers.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _application = new HttpLifecycleApplication(Transport, PayloadLength);
        _application.QualifyAsync().GetAwaiter().GetResult();
    }

    /// <summary>Includes JSON parsing, generated binding, dispatch and response writing.</summary>
    [Benchmark]
    public Task<int> Json() => _application.SendAsync("json", KnownLength);

    /// <summary>Includes scalar form parsing and generated binding.</summary>
    [Benchmark]
    public Task<int> Form() => _application.SendAsync("form", KnownLength);

    /// <summary>Includes a custom asynchronous binder that consumes the replayable body.</summary>
    [Benchmark]
    public Task<int> CustomBinder() => _application.SendAsync("custom", KnownLength);

    /// <summary>Stops the server and releases owned clients.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _application?.Dispose();
        GC.SuppressFinalize(this);
    }
}

sealed class HttpLifecycleApplication : IDisposable
{
    const int MaximumBodyBytes = 128 * 1024;
    readonly WebApplication _app;
    readonly HttpClient _client;
    readonly byte[] _json;
    readonly byte[] _form;
    readonly byte[] _custom;
    readonly HttpLifecycleState _state = new();
    readonly int _expected;
    readonly string _transport;

    internal HttpLifecycleApplication(string transport, int payloadLength)
    {
        _transport = transport;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (transport == "inprocess")
            builder.WebHost.UseTestServer();
        else
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_state);
        builder.Services.Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = MaximumBodyBytes);
        builder.Services.AddPortia().AddHttp().AddRequestHandler<HttpLifecycleHandler>().AddRequestHandler<HttpCustomLifecycleHandler>();
        _app = builder.Build();
        _app.MapPortiaPost<HttpLifecycleRequest, int>("/json").DisableAntiforgery();
        _app.MapPortiaPost<HttpCustomLifecycleRequest, int>("/custom", endpoint => endpoint
            .Accepts<string>("text/plain").OnBind(async (context, ct) =>
            {
                Interlocked.Increment(ref _state.Binders);
                if (!context.Request.Body.CanSeek || context.Request.Body.Position != 0)
                    throw new InvalidOperationException("Custom binder did not receive a replayable body at the beginning.");
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                return new HttpCustomLifecycleRequest(await reader.ReadToEndAsync(ct), 7);
            })).DisableAntiforgery();
        _app.StartAsync().GetAwaiter().GetResult();
        _client = transport == "inprocess" ? _app.GetTestClient() : new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
        var text = new string('a', payloadLength);
        _expected = payloadLength + 7;
        _json = JsonSerializer.SerializeToUtf8Bytes(new HttpLifecycleRequest(text, 7), HttpLifecycleJsonContext.Default.HttpLifecycleRequest);
        _form = Encoding.UTF8.GetBytes($"payload={text}&number=7");
        _custom = Encoding.UTF8.GetBytes(text);
    }

    internal async Task<int> SendAsync(string mode, bool knownLength)
    {
        var bytes = mode switch { "json" => _json, "form" => _form, _ => _custom };
        using var request = new HttpRequestMessage(HttpMethod.Post, mode == "custom" ? "/custom" : "/json")
        {
            Content = new PreparedBody(bytes, knownLength)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mode switch
        {
            "json" => "application/json",
            "form" => "application/x-www-form-urlencoded",
            _ => "text/plain"
        });
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(), HttpLifecycleJsonContext.Default.Int32);
        if (result != _expected)
            throw new InvalidOperationException("Generated endpoint did not consume the prepared body.");
        return result;
    }

    internal async Task QualifyAsync()
    {
        foreach (var mode in new[] { "json", "form", "custom" })
        {
            await SendAsync(mode, true);
            await SendAsync(mode, false);
        }
        foreach (var knownLength in new[] { true, false })
        {
            var before = _state.Handlers;
            var binders = _state.Binders;
            using var content = new PreparedBody(new byte[MaximumBodyBytes + 1], knownLength);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            using var response = await _client.PostAsync("/custom", content);
            if (response.StatusCode != HttpStatusCode.RequestEntityTooLarge || _state.Handlers != before || _state.Binders != binders)
                throw new InvalidOperationException("Oversized body reached a custom binder or handler.");
            // Pinned Kestrel counts chunk framing against MaxRequestBodySize. Keep that server
            // boundary visible rather than weakening Portia's cap to accept a larger wire body.
            var nearLimit = !knownLength && _transport == "loopback" ? MaximumBodyBytes - 1024 : MaximumBodyBytes;
            using var boundary = new PreparedBody(Encoding.UTF8.GetBytes(new string('a', nearLimit)), knownLength);
            boundary.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            using var accepted = await _client.PostAsync("/custom", boundary);
            if (!accepted.IsSuccessStatusCode)
                throw new InvalidOperationException($"At-limit custom body failed on {_transport}, knownLength={knownLength}: {(int)accepted.StatusCode}, {await accepted.Content.ReadAsStringAsync()}; binders={_state.Binders}.");
            if (JsonSerializer.Deserialize(await accepted.Content.ReadAsByteArrayAsync(), HttpLifecycleJsonContext.Default.Int32) != nearLimit + 7)
                throw new InvalidOperationException("An exactly-at-limit body lost replayed content.");
            if (!knownLength && _transport == "loopback")
            {
                before = _state.Handlers;
                binders = _state.Binders;
                using var framedBoundary = new PreparedBody(Encoding.UTF8.GetBytes(new string('a', MaximumBodyBytes)), false);
                framedBoundary.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                using var framedRejection = await _client.PostAsync("/custom", framedBoundary);
                if (framedRejection.StatusCode != HttpStatusCode.RequestEntityTooLarge || _state.Handlers != before || _state.Binders != binders)
                    throw new InvalidOperationException("Kestrel's chunk framing limit did not reject before binding.");
            }
        }
        var handlers = _state.Handlers;
        using var unsupported = new StringContent("payload", Encoding.UTF8, "application/xml");
        using var rejected = await _client.PostAsync("/json", unsupported);
        if (rejected.StatusCode != HttpStatusCode.UnsupportedMediaType || handlers != _state.Handlers)
            throw new InvalidOperationException("Unsupported media reached the handler.");
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.StopAsync().GetAwaiter().GetResult();
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

sealed class PreparedBody(byte[] bytes, bool knownLength) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = bytes.Length; return knownLength; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
}

sealed class HttpLifecycleState
{
    internal int Handlers;
    internal int Binders;
}

[Discriminator("benchmark.http.lifecycle")]
sealed record HttpLifecycleRequest(string Payload, int Number) : IRequest<int>, ICallable;

[Discriminator("benchmark.http.custom.lifecycle")]
sealed record HttpCustomLifecycleRequest(string Payload, int Number) : IRequest<int>, ICallable;

sealed class HttpCustomLifecycleHandler(HttpLifecycleState state) : IRequestHandler<HttpCustomLifecycleRequest, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<HttpCustomLifecycleRequest> context, CancellationToken ct)
    {
        Interlocked.Increment(ref state.Handlers);
        return ValueTask.FromResult(Result<int>.Success(context.Request.Payload.Length + context.Request.Number));
    }
}

sealed class HttpLifecycleHandler(HttpLifecycleState state) : IRequestHandler<HttpLifecycleRequest, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<HttpLifecycleRequest> context, CancellationToken ct)
    {
        Interlocked.Increment(ref state.Handlers);
        return ValueTask.FromResult(Result<int>.Success(context.Request.Payload.Length + context.Request.Number));
    }
}

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HttpLifecycleRequest))]
[JsonSerializable(typeof(HttpCustomLifecycleRequest))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
sealed partial class HttpLifecycleJsonContext : JsonSerializerContext;

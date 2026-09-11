using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Covers the document being composed once rather than per request. Composing it walks every
///     described endpoint and resolves a schema per parameter and response, and the result is the
///     same for every caller, so repeating that work on an unauthenticated route is both waste and
///     an amplification anyone can reach.
/// </summary>
public sealed class OpenApiDocumentCachingTests : IAsyncDisposable
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
    ///     A warm request should cost about what writing the document costs and no more. The budget
    ///     is stated against the document's own size so it stays meaningful as the surface grows;
    ///     regenerating per request costs an order of magnitude more than writing it.
    /// </summary>
    [Fact]
    public async Task ShouldNotRecomposeTheDocumentOnEveryRequest()
    {
        const int iterations = 100;
        var client = await StartAsync();

        var first = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var documentBytes = (await first.Content.ReadAsByteArrayAsync()).Length;
        for (var i = 0; i < 10; i++)
            _ = await client.GetAsync("/openapi/v1.json");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < iterations; i++)
            _ = await client.GetAsync("/openapi/v1.json");
        var allocated = (GC.GetTotalAllocatedBytes(true) - before) / iterations;

        Assert.True(allocated < documentBytes * 4L,
            $"A warm request allocated {allocated} bytes for a {documentBytes}-byte document.");
    }

    /// <summary>The cached bytes keep the media types and the 404 for an unknown document name.</summary>
    [Fact]
    public async Task ShouldServeTheDocumentedMediaTypesAndRejectUnknownDocumentNames()
    {
        var client = await StartAsync();

        var json = await client.GetAsync("/openapi/v1.json");
        var yaml = await client.GetAsync("/openapi/v1.yml");
        var unknown = await client.GetAsync("/openapi/v2.json");

        Assert.Equal("application/json; charset=utf-8", json.Content.Headers.ContentType?.ToString());
        Assert.Equal("text/plain+yaml; charset=utf-8", yaml.Content.Headers.ContentType?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(json.Content.Headers.ContentLength, (await json.Content.ReadAsByteArrayAsync()).Length);
    }

    /// <summary>
    ///     A document that cannot be composed must fail every request. Caching the first failure
    ///     would be indistinguishable here, but caching a partial render would not be, so nothing is
    ///     published until both representations are rendered.
    /// </summary>
    [Fact]
    public async Task ShouldFailEveryRequestGivenTheDocumentCannotBeComposed()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _app = builder.Build();
        _ = _app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");
        _ = _app.MapGet("/ordinary", () => Results.Ok()).WithName("httpGetWidget");
        await _app.StartAsync();
        using var client = _app.GetTestClient();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.GetAsync("/openapi/v1.json"));
            Assert.Contains("is duplicated", failure.Message, StringComparison.Ordinal);
        }
    }

    async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _app = builder.Build();
        var group = _app.MapGroup("/api");
        _ = group.MapPortiaPost<HttpCreateOrder, Uuid>("/orders");
        _ = group.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");
        _ = group.MapPortiaPost<HttpSendPing>("/ping");
        _ = group.MapPortiaGetStream<HttpListWidgets, string>("/widgets");
        _ = group.MapPortiaPost<HttpCreatePayment, Uuid>("/payments");
        await _app.StartAsync();
        return _app.GetTestClient();
    }
}

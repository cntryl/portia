using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Browsers attach cookies and other ambient credentials to requests that another site starts, so a
///     generated endpoint accepts a state-changing browser request only from its own origin or from an
///     origin the application's ASP.NET Core CORS pipeline allows.
/// </summary>
public sealed class HttpCrossOriginTests : IAsyncDisposable
{
    const string Attacker = "https://attacker.example";
    const string Trusted = "https://app.example";
    const string Admin = "https://admin.example";

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
    ///     Verifies every unsafe method from another origin is refused before binding runs, including a
    ///     bodiless post that no content-type rule can catch and an endpoint with a custom binder.
    /// </summary>
    [Fact]
    public async Task ShouldRejectCrossOriginUnsafeRequestsBeforeBinding()
    {
        var binderCalls = 0;
        var client = await StartAsync(app =>
        {
            _ = app.MapPortiaPost<HttpOptionalBody, string>("/optional");
            _ = app.MapPortiaPut<HttpCrossOriginReplace, string>("/optional");
            _ = app.MapPortiaPatch<HttpCrossOriginPatch, string>("/optional");
            _ = app.MapPortiaDelete<HttpCrossOriginRemove, string>("/optional");
            _ = app.MapPortiaPost<HttpCrossOriginCustom, string>("/custom", endpoint => endpoint
                .NoInput()
                .OnBind(_ =>
                {
                    binderCalls++;
                    return new HttpCrossOriginCustom();
                }));
        });

        HttpResponseMessage[] responses =
        [
            await client.SendAsync(Request(HttpMethod.Post, "/optional", "cross-site", Attacker)),
            await client.SendAsync(Request(HttpMethod.Post, "/optional", "same-site", "https://sub.localhost")),
            await client.SendAsync(Request(HttpMethod.Post, "/optional", null, Attacker)),
            await client.SendAsync(Request(HttpMethod.Post, "/optional", null, "null")),
            await client.SendAsync(Request(HttpMethod.Put, "/optional", "cross-site", Attacker)),
            await client.SendAsync(Request(HttpMethod.Patch, "/optional", "cross-site", Attacker)),
            await client.SendAsync(Request(HttpMethod.Delete, "/optional", "cross-site", Attacker)),
            await client.SendAsync(Request(HttpMethod.Post, "/custom", "cross-site", Attacker))
        ];

        foreach (var response in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {(int)response.StatusCode}.");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(0, binderCalls);
    }

    /// <summary>
    ///     Verifies same-origin browser requests, user-initiated requests, clients that are not browsers,
    ///     and safe methods from any origin are unaffected.
    /// </summary>
    [Fact]
    public async Task ShouldAllowSameOriginNonBrowserAndSafeRequests()
    {
        var client = await StartAsync(app =>
        {
            _ = app.MapPortiaPost<HttpOptionalBody, string>("/optional");
            _ = app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");
        });

        HttpResponseMessage[] responses =
        [
            await client.SendAsync(Request(HttpMethod.Post, "/optional", null, null)),
            await client.SendAsync(Request(HttpMethod.Post, "/optional", "same-origin", "http://localhost")),
            await client.SendAsync(Request(HttpMethod.Post, "/optional", "none", null)),
            await client.SendAsync(Request(HttpMethod.Post, "/optional", null, "http://localhost")),
            await client.SendAsync(Request(HttpMethod.Get, $"/widgets/{Uuid.CreateVersion4()}", "cross-site", Attacker))
        ];

        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode,
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {(int)response.StatusCode}.");
        }
    }

    /// <summary>
    ///     Verifies a request without fetch metadata falls back to comparing its <c>Origin</c> with the
    ///     request host, including the port and the scheme's default port.
    /// </summary>
    /// <param name="host">The request host.</param>
    /// <param name="origin">The browser-supplied origin.</param>
    /// <param name="allowed">Whether the request is same-origin.</param>
    [Theory]
    [InlineData("localhost:5000", "http://localhost:5000", true)]
    [InlineData("localhost:5000", "http://localhost:5001", false)]
    [InlineData("localhost:5000", "http://localhost", false)]
    [InlineData("example.com", "https://example.com", true)]
    [InlineData("example.com", "https://example.com:443", true)]
    [InlineData("example.com:443", "https://example.com", true)]
    [InlineData("example.com", "https://example.com:8443", false)]
    [InlineData("EXAMPLE.com", "https://example.com", true)]
    [InlineData("[::1]:5000", "http://[::1]:5000", true)]
    [InlineData("example.com", "https://example.com.attacker.example", false)]
    [InlineData("example.com", "ftp://example.com", false)]
    [InlineData("example.com", "null", false)]
    [InlineData("example.com", "", false)]
    public void ShouldCompareOriginWithRequestHostWithoutFetchMetadata(string host, string origin, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Host = new HostString(host);
        context.Request.Headers.Origin = origin;

        Assert.Equal(allowed, PortiaHttpBinding.RejectCrossOrigin(context) is null);
    }

    /// <summary>
    ///     Verifies an origin is trusted exactly when the CORS pipeline allows it for that endpoint: a
    ///     named middleware policy, an endpoint policy that overrides it, and an endpoint that disables
    ///     CORS, regardless of whether CORS services are registered before or after Portia's.
    /// </summary>
    /// <param name="corsRegisteredFirst">Whether <c>AddCors</c> runs before <c>AddHttp</c>.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldTrustOriginsTheCorsPipelineAllows(bool corsRegisteredFirst)
    {
        var client = await StartAsync(app =>
            {
                _ = app.UseCors("spa");
                _ = app.MapPortiaPost<HttpOptionalBody, string>("/middleware-policy");
                _ = app.MapPortiaPut<HttpCrossOriginReplace, string>("/endpoint-policy").RequireCors("admin");
                _ = app.MapPortiaPatch<HttpCrossOriginPatch, string>("/cors-disabled")
                    .WithMetadata(new DisableCorsAttribute());
            },
            services => services.AddCors(options =>
            {
                options.AddPolicy("spa", policy => policy.WithOrigins(Trusted).AllowCredentials());
                options.AddPolicy("admin", policy => policy.WithOrigins(Admin).AllowCredentials());
            }),
            corsRegisteredFirst);

        using var trusted =
            await client.SendAsync(Request(HttpMethod.Post, "/middleware-policy", "cross-site", Trusted));
        using var attacker =
            await client.SendAsync(Request(HttpMethod.Post, "/middleware-policy", "cross-site", Attacker));
        using var endpointTrusted =
            await client.SendAsync(Request(HttpMethod.Put, "/endpoint-policy", "cross-site", Admin));
        using var endpointOverridden =
            await client.SendAsync(Request(HttpMethod.Put, "/endpoint-policy", "cross-site", Trusted));
        using var disabled = await client.SendAsync(Request(HttpMethod.Patch, "/cors-disabled", "cross-site", Trusted));

        Assert.Equal(HttpStatusCode.OK, trusted.StatusCode);
        Assert.Equal(Trusted, Assert.Single(trusted.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal(HttpStatusCode.Forbidden, attacker.StatusCode);
        Assert.Equal(HttpStatusCode.OK, endpointTrusted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, endpointOverridden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
    }

    /// <summary>Verifies the default CORS policy applied by <c>UseCors()</c> is honored the same way.</summary>
    [Fact]
    public async Task ShouldTrustOriginsTheDefaultCorsPolicyAllows()
    {
        var client = await StartAsync(app =>
            {
                _ = app.UseCors();
                _ = app.MapPortiaPost<HttpOptionalBody, string>("/optional");
            },
            services => services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(Trusted))));

        using var trusted = await client.SendAsync(Request(HttpMethod.Post, "/optional", "same-site", Trusted));
        using var attacker = await client.SendAsync(Request(HttpMethod.Post, "/optional", "same-site", Attacker));

        Assert.Equal(HttpStatusCode.OK, trusted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, attacker.StatusCode);
    }

    /// <summary>
    ///     Verifies generated endpoints require <c>AddHttp()</c>, which is what lets them see the CORS
    ///     pipeline's decisions, rather than silently refusing every trusted origin.
    /// </summary>
    [Fact]
    public void ShouldRequireAddHttpBeforeMappingEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.Services.AddPortia();
        _app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _app.MapPortiaPost<HttpOptionalBody, string>("/optional"));

        Assert.Contains("AddHttp()", exception.Message, StringComparison.Ordinal);
    }

    static HttpRequestMessage Request(HttpMethod method, string path, string? fetchSite, string? origin)
    {
        var request = new HttpRequestMessage(method, path);
        if (fetchSite is not null)
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return request;
    }

    async Task<HttpClient> StartAsync(Action<WebApplication> map, Action<IServiceCollection>? configureCors = null,
        bool corsRegisteredFirst = true)
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        if (corsRegisteredFirst)
            configureCors?.Invoke(builder.Services);
        _ = builder.Services.AddFrameworkTests();
        _ = builder.Services.AddPortia().AddHttp()
            .AddRequestHandler<HttpCrossOriginReplaceHandler>()
            .AddRequestHandler<HttpCrossOriginPatchHandler>()
            .AddRequestHandler<HttpCrossOriginRemoveHandler>()
            .AddRequestHandler<HttpCrossOriginCustomHandler>();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        if (!corsRegisteredFirst)
            configureCors?.Invoke(builder.Services);
        _app = builder.Build();
        map(_app);
        await _app.StartAsync();
        return _app.GetTestClient();
    }
}

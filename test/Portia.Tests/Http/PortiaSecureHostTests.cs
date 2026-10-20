using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

/// <summary>Verifies Portia's fail-closed ASP.NET host profile through a real TestServer.</summary>
public sealed class PortiaSecureHostTests
{
    const string TestScheme = "PortiaSecureHostTest";

    /// <summary>Generated request and OpenAPI endpoints reject anonymous callers.</summary>
    [Fact]
    public async Task ShouldRequireHostAuthenticationForGeneratedRequestsAndOpenApi()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestScheme;
                options.DefaultChallengeScheme = TestScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestScheme, _ => { });
        builder.Services.AddPortia()
            .RequireSecureHost(options => options.AllowAnonymous<HttpGetWidget>())
            .AddRequestHandler<HttpGetWidgetHandler>();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");
        app.MapPortiaOpenApi();
        await app.StartAsync();
        var client = app.GetTestClient();

        var denied = await client.GetAsync($"/widgets/{Uuid.CreateVersion4()}");
        var openApiDenied = await client.GetAsync("/openapi/v1.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/widgets/{Uuid.CreateVersion4()}");
        request.Headers.Add("X-Test-Authenticated", "true");
        var allowed = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, openApiDenied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>A public route needs both explicit endpoint and request-level anonymous allowances.</summary>
    [Fact]
    public async Task ShouldAllowExplicitlyPublicPortiaRoute()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestScheme, _ => { });
        builder.Services.AddPortia()
            .RequireSecureHost(options => options.AllowAnonymous<HttpGetWidget>())
            .AddRequestHandler<HttpGetWidgetHandler>();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}").AllowAnonymous();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync($"/widgets/{Uuid.CreateVersion4()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The secure host profile rejects an HTTP host without a default authentication scheme.</summary>
    [Fact]
    public async Task ShouldFailStartupWhenSecureHostHasNoDefaultAuthenticationScheme()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        builder.Services.AddPortia()
            .RequireSecureHost(options => options.AllowAnonymous<HttpGetWidget>())
            .AddRequestHandler<HttpGetWidgetHandler>();

        await using var app = builder.Build();
        app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("default ASP.NET authentication and challenge scheme", error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Secure hosts reject GET routes whose request contract does not declare read-only behavior.</summary>
    [Fact]
    public async Task ShouldFailStartupWhenGetRequestIsNotReadOnly()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestScheme, _ => { });
        builder.Services.AddPortia()
            .RequireSecureHost(options => options.AllowAnonymous<HttpUpdateWidget>())
            .AddRequestHandler<HttpUpdateWidgetHandler>();

        await using var app = builder.Build();
        app.MapPortiaGet<HttpUpdateWidget, string>("/widgets/{widget_id}");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("IReadOnlyRequest", error.Message, StringComparison.Ordinal);
        Assert.Contains("GET endpoints", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Secure hosts reject Portia endpoints whose authorization metadata was removed.</summary>
    [Fact]
    public async Task ShouldFailStartupWhenPortiaEndpointLacksAuthorizationMetadata()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestScheme, _ => { });
        builder.Services.AddPortia()
            .RequireSecureHost(options => options.AllowAnonymous<HttpGetWidget>())
            .AddRequestHandler<HttpGetWidgetHandler>();

        await using var app = builder.Build();
        app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}").Add(endpoint =>
        {
            foreach (var authorization in endpoint.Metadata.OfType<IAuthorizeData>().ToArray())
                endpoint.Metadata.Remove(authorization);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("without ASP.NET authorization metadata", error.Message, StringComparison.Ordinal);
    }

    sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Authenticated"))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "secure-host-test")], Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

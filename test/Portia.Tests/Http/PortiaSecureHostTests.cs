using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    /// <summary>OpenAPI routes can receive an explicit ASP.NET endpoint authorization convention.</summary>
    [Fact]
    public async Task ShouldAllowOpenApiRoutesToOverrideSecureHostAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestScheme, _ => { });
        builder.Services.AddPortia().RequireSecureHost();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPortiaOpenApi(endpoint => endpoint.AllowAnonymous());
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/openapi/v1.json");
        var yamlResponse = await client.GetAsync("/openapi/v1.yml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, yamlResponse.StatusCode);
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

    /// <summary>The secure host profile rejects a mapped endpoint without a default authentication scheme.</summary>
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

    /// <summary>A worker-only secure composition does not require HTTP authentication setup.</summary>
    [Fact]
    public async Task ShouldNotRequireAuthenticationSchemeWhenNoPortiaHttpEndpointsAreMapped()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        builder.Services.AddPortia()
            .RequireSecureHost(options => options.AllowAnonymous<HttpGetWidget>())
            .AddRequestHandler<HttpGetWidgetHandler>();

        await using var app = builder.Build();

        await app.StartAsync();

        Assert.True(app.Lifetime.ApplicationStarted.IsCancellationRequested);
    }

    /// <summary>Secure hosts reject GET routes whose request contract does not declare read-only behavior.</summary>
    [Fact]
    public async Task ShouldFailMappingWhenGetRequestIsNotReadOnly()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestScheme, _ => { });
        builder.Services.AddPortia()
            .RequireSecureHost(options => options.AllowAnonymous<HttpUpdateWidget>())
            .AddRequestHandler<HttpUpdateWidgetHandler>();

        await using var app = builder.Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            app.MapPortiaGet<HttpUpdateWidget, string>("/widgets/{widget_id}"));

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
        app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}",
                endpoint => endpoint.FromQuery(request => request.IncludeArchived))
            .Add(endpoint =>
            {
                foreach (var authorization in endpoint.Metadata.OfType<IAuthorizeData>().ToArray())
                    endpoint.Metadata.Remove(authorization);
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("without ASP.NET authorization metadata", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Grouped Portia and ordinary routes are validated through the complete route group.</summary>
    [Fact]
    public async Task ShouldBuildGroupedSecureEndpointsWithTheirCompleteRoutePattern()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestScheme, _ => { });
        builder.Services.AddPortia()
            .RequireSecureHost(options => options.AllowAnonymous<HttpGetWidget>())
            .AddRequestHandler<HttpGetWidgetHandler>();

        await using var app = builder.Build();
        var group = app.MapGroup("/tenants/{tenant_id}");
        group.MapGet("/metadata", (string tenant_id) => tenant_id);
        group.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");

        await app.StartAsync();
        var response = await app.GetTestClient().GetAsync("/tenants/acme/metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acme", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Validation registered after other services still fails before those services start.</summary>
    [Fact]
    public async Task ShouldValidateEndpointAuthorizationBeforeEarlierHostedServicesStart()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        var portia = builder.Services.AddPortia();
        var probe = new HostedServiceProbe();
        builder.Services.AddSingleton(probe);
        builder.Services.AddHostedService(provider => provider.GetRequiredService<HostedServiceProbe>());
        _ = builder.Services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestScheme, _ => { });
        portia.RequireSecureHost(options => options.AllowAnonymous<HttpGetWidget>())
            .AddRequestHandler<HttpGetWidgetHandler>();

        await using var app = builder.Build();
        app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}",
                endpoint => endpoint.FromQuery(request => request.IncludeArchived))
            .Add(endpoint =>
            {
                foreach (var authorization in endpoint.Metadata.OfType<IAuthorizeData>().ToArray())
                    endpoint.Metadata.Remove(authorization);
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("without ASP.NET authorization metadata", error.Message, StringComparison.Ordinal);
        Assert.False(probe.Started);
    }

    /// <summary>Portia endpoints mapped by the legacy Startup pipeline are validated when that pipeline builds.</summary>
    [Fact]
    public async Task ShouldValidatePortiaEndpointsMappedDuringWebHostStartup()
    {
#pragma warning disable ASPDEPR004, ASPDEPR008
        using var host = new WebHostBuilder()
            .UseTestServer()
            .UseStartup<LateUnsafeGetStartup>()
            .Build();
#pragma warning restore ASPDEPR004, ASPDEPR008

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("IReadOnlyRequest", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Late endpoint mapping still runs registered secure-host startup checks.</summary>
    [Fact]
    public async Task ShouldValidateAuthenticationSchemeForEndpointsMappedDuringWebHostStartup()
    {
#pragma warning disable ASPDEPR004, ASPDEPR008
        using var host = new WebHostBuilder()
            .UseTestServer()
            .UseStartup<LateMissingAuthenticationStartup>()
            .Build();
#pragma warning restore ASPDEPR004, ASPDEPR008

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("default ASP.NET authentication and challenge scheme", error.Message,
            StringComparison.Ordinal);
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

    sealed class HostedServiceProbe : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    /// <summary>Maps an unsafe GET from the legacy ASP.NET Core startup hook.</summary>
    public sealed class LateUnsafeGetStartup
    {
        /// <summary>Registers a secure Portia application for the legacy startup test.</summary>
        [SuppressMessage("Performance", "CA1822",
            Justification = "ASP.NET Core discovers this conventional startup method by reflection.")]
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();
            _ = services.AddAuthentication(TestScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestScheme, _ => { });
            services.AddPortia()
                .RequireSecureHost()
                .AddRequestHandler<HttpUpdateWidgetHandler>();
        }

        /// <summary>Maps an unsafe GET when the generic web host builds its application pipeline.</summary>
        [SuppressMessage("Performance", "CA1822",
            Justification = "ASP.NET Core discovers this conventional startup method by reflection.")]
        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
                endpoints.MapPortiaGet<HttpUpdateWidget, string>("/widgets/{widget_id}"));
        }
    }

    /// <summary>Maps a safe Portia GET without authentication during legacy startup.</summary>
    public sealed class LateMissingAuthenticationStartup
    {
        /// <summary>Registers a secure Portia application without an ASP.NET scheme.</summary>
        [SuppressMessage("Performance", "CA1822",
            Justification = "ASP.NET Core discovers this conventional startup method by reflection.")]
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();
            services.AddPortia()
                .RequireSecureHost(options => options.AllowAnonymous<HttpGetWidget>())
                .AddRequestHandler<HttpGetWidgetHandler>();
        }

        /// <summary>Maps a safe GET when the generic web host builds its application pipeline.</summary>
        [SuppressMessage("Performance", "CA1822",
            Justification = "ASP.NET Core discovers this conventional startup method by reflection.")]
        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
                endpoints.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}"));
        }
    }
}

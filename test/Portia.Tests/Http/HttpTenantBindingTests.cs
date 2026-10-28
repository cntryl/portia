using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Verifies a request can declare <see cref="TenantId" /> as a route or body member: a well-formed
///     tenant binds, a malformed one is a 400 at the boundary rather than an exception from the stream
///     address inside the handler, and OpenAPI describes it as a string.
/// </summary>
public sealed class HttpTenantBindingTests : IAsyncDisposable
{
    WebApplication? _app;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    /// <summary>A route token binds to the typed tenant.</summary>
    [Fact]
    public async Task ShouldBindTenantFromRoute()
    {
        var client = await StartAsync();

        using var response = await client.PostAsJsonAsync("/tenants/acme/probe", new { name = "savings" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acme:savings", await response.Content.ReadFromJsonAsync<string>());
    }

    /// <summary>A route token that is not a realm segment is rejected before the handler runs.</summary>
    [Fact]
    public async Task ShouldRejectMalformedTenantInRoute()
    {
        var client = await StartAsync();

        using var response = await client.PostAsJsonAsync("/tenants/a*b/probe", new { name = "savings" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A body member binds to the typed tenant, and a malformed one is a 400.</summary>
    /// <param name="tenant">The tenant text sent in the body.</param>
    /// <param name="expected">The expected status.</param>
    [Theory]
    [InlineData("acme", HttpStatusCode.OK)]
    [InlineData("a/b", HttpStatusCode.BadRequest)]
    [InlineData("{tenant}", HttpStatusCode.BadRequest)]
    public async Task ShouldBindTenantFromBody(string tenant, HttpStatusCode expected)
    {
        var client = await StartAsync();

        using var response = await client.PostAsJsonAsync("/probe", new { tenant, name = "savings" });

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>OpenAPI describes the tenant as a string in both the path and the body.</summary>
    [Fact]
    public async Task ShouldDescribeTenantAsString()
    {
        var client = await StartAsync();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        var routed = document.RootElement.GetProperty("paths").GetProperty("/tenants/{tenant}/probe")
            .GetProperty("post");
        var parameter = Assert.Single(routed.GetProperty("parameters").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "tenant");
        Assert.Equal("string", parameter.GetProperty("schema").GetProperty("type").GetString());
        var body = document.RootElement.GetProperty("paths").GetProperty("/probe").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("string", body.GetProperty("properties").GetProperty("tenant").GetProperty("type").GetString());
    }

    async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _ = builder.Services.AddFrameworkTests();
        _app = builder.Build();
        _ = _app.MapPortiaOpenApi();
        _ = _app.MapPortiaPost<HttpTenantProbe, string>("/tenants/{tenant}/probe");
        _ = _app.MapPortiaPost<HttpTenantBodyProbe, string>("/probe");
        await _app.StartAsync();
        return _app.GetTestClient();
    }
}

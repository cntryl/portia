using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cntryl.Portia;

/// <summary>End-to-end Microsoft OpenAPI document coverage.</summary>
public sealed class OpenApiTests : IAsyncDisposable
{
    WebApplication? _app;

    /// <summary>Both zero-configuration routes serve equivalent valid OpenAPI 3.1 documents.</summary>
    [Fact]
    public async Task ShouldServeEquivalentOpenApi31DocumentsGivenPortiaApplicationWhenJsonAndYamlAreRequested()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        _app = builder.Build();

        var group = _app.MapGroup("/api").WithTags("orders");
        _ = group.MapPortiaPost<HttpCreateOrder, Uuid>("/orders")
            .WithSummary("Create an order")
            .WithDescription("Creates one order.");
        _ = group.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");
        _ = group.MapPortiaPost<HttpSendPing>("/ping");
        _ = group.MapPortiaGetStream<HttpListWidgets, string>("/widgets");
        _ = group.MapPortiaGetSse<HttpListWidgets, string>("/widget-events").ExcludeFromDescription();
        _ = _app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithName("health");
        await _app.StartAsync();

        using var client = _app.GetTestClient();
        var jsonResponse = await client.GetAsync("/openapi/v1.json");
        var yamlResponse = await client.GetAsync("/openapi/v1.yml");
        Assert.Equal(HttpStatusCode.OK, jsonResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, yamlResponse.StatusCode);

        var json = await jsonResponse.Content.ReadAsStringAsync();
        var yaml = await yamlResponse.Content.ReadAsStringAsync();
        var jsonResult = OpenApiDocument.Parse(json, "json", new OpenApiReaderSettings());
        var yamlSettings = new OpenApiReaderSettings();
        yamlSettings.AddYamlReader();
        var yamlResult = OpenApiDocument.Parse(yaml, "yaml", yamlSettings);
        var jsonDiagnostic = Assert.IsType<OpenApiDiagnostic>(jsonResult.Diagnostic);
        var yamlDiagnostic = Assert.IsType<OpenApiDiagnostic>(yamlResult.Diagnostic);
        var jsonDocument = Assert.IsType<OpenApiDocument>(jsonResult.Document);
        var yamlDocument = Assert.IsType<OpenApiDocument>(yamlResult.Document);
        Assert.Empty(jsonDiagnostic.Errors);
        Assert.Empty(yamlDiagnostic.Errors);
        Assert.Equal(OpenApiSpecVersion.OpenApi3_1, jsonDiagnostic.SpecificationVersion);
        Assert.Equal(OpenApiSpecVersion.OpenApi3_1, yamlDiagnostic.SpecificationVersion);
        Assert.Equal(jsonDocument.Paths.Keys.Order(), yamlDocument.Paths.Keys.Order());

        using var raw = JsonDocument.Parse(json);
        Assert.Equal("3.1.1", raw.RootElement.GetProperty("openapi").GetString());
        var paths = raw.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/orders", out var createPath), json);
        var create = createPath.GetProperty("post");
        Assert.Equal("httpCreateOrder", create.GetProperty("operationId").GetString());
        Assert.Equal("Create an order", create.GetProperty("summary").GetString());
        Assert.True(create.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("properties").TryGetProperty("lines", out _));
        var get = paths.GetProperty("/api/widgets/{widget_id}").GetProperty("get");
        Assert.Contains(get.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "widget_id" && parameter.GetProperty("in").GetString() == "path");
        Assert.Contains(get.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "include_archived" && parameter.GetProperty("in").GetString() == "query");
        Assert.Contains(get.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "include_archived"
            && parameter.GetProperty("schema").GetProperty("default").ValueKind == JsonValueKind.False);
        Assert.True(paths.GetProperty("/api/ping").GetProperty("post").GetProperty("responses").TryGetProperty("202", out _));
        var responses = create.GetProperty("responses");
        var problemSchema = responses.GetProperty("400").GetProperty("content")
            .GetProperty("application/problem+json").GetProperty("schema");
        Assert.True(problemSchema.GetProperty("properties").TryGetProperty("status", out _));
        Assert.True(problemSchema.GetProperty("properties").TryGetProperty("detail", out _));
        Assert.False(problemSchema.GetProperty("properties").TryGetProperty("message", out _));
        Assert.True(responses.GetProperty("400").GetProperty("headers")
            .TryGetProperty(ResultHttpExtensions.TransientHeaderName, out _));
        Assert.True(responses.GetProperty("401").GetProperty("headers").TryGetProperty("WWW-Authenticate", out _));
        Assert.True(paths.TryGetProperty("/health", out _));
        Assert.False(paths.TryGetProperty("/api/widget-events", out _));
    }

    /// <summary>Cross-generator operation ID collisions fail document generation deterministically.</summary>
    [Fact]
    public async Task ShouldKeepApplicationRunningGivenCollisionWhenOpenApiDocumentFails()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        _app = builder.Build();
        _ = _app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");
        _ = _app.MapGet("/ordinary", () => Results.Ok()).WithName("httpGetWidget");
        await _app.StartAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _app.GetTestClient().GetAsync("/openapi/v1.json"));
        Assert.Contains("operationId 'httpGetWidget' is duplicated", exception.Message, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}

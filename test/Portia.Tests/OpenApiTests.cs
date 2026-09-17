using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cntryl.Portia;

/// <summary>End-to-end Microsoft OpenAPI document coverage.</summary>
public sealed class OpenApiTests : IAsyncDisposable
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

    /// <summary>HTTP activation never replaces the application's global ASP.NET JSON policy.</summary>
    [Fact]
    public void ShouldLeaveAspNetCoreJsonOptionsOwnedByTheApplication()
    {
        var services = new ServiceCollection();
        _ = services.Configure<JsonOptions>(options => options.SerializerOptions.WriteIndented = true);
        _ = services.AddPortia().ConfigureJson(options => options.WriteIndented = false).AddHttp();
        using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions.WriteIndented);
        Assert.False(provider.GetRequiredService<JsonSerializerOptions>().WriteIndented);
    }

    /// <summary>Portia schemas describe its payloads while ordinary endpoints retain their own contract.</summary>
    [Fact]
    public async Task ShouldDescribePortiaSerializerContractsSeparately()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.Configure<JsonOptions>(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        _ = builder.Services.AddPortia().AddHttp()
            .AddRequestHandler<OpenApiContractHandler>()
            .AddRequestHandler<OpenApiContractStreamHandler>()
            .ConfigureJson(options =>
            {
                options.Converters.Add(new JsonStringEnumConverter<DayOfWeek>());
                options.Converters.Add(new OpenApiMoneyConverter());
            });
        _app = builder.Build();
        _ = _app.MapPortiaOpenApi();
        _ = _app.MapPortiaPost<OpenApiContractRequest, OpenApiContractNode>("/contract")
            .WithMetadata(new ProducesResponseTypeMetadata(
                StatusCodes.Status400BadRequest, typeof(string), ["text/plain"]));
        _ = _app.MapPortiaGetStream<OpenApiContractStream, OpenApiContractNode>("/contract-stream");
        _ = _app.MapGet("/ordinary-contract",
            () => new OpenApiContractNode("ordinary", "explicit", DayOfWeek.Monday, new HttpMoney("USD", 42),
                Uuid.CreateVersion4(), null));
        await _app.StartAsync();
        using var client = _app.GetTestClient();
        using var response = await client.PostAsync("/contract", new StringContent(
            """{"payload":{"display_name":"nested","wire-name":"explicit","day_value":"Monday","custom_value":"42","team_id":"21f7f8de-8051-5b89-8680-0195ef798b6a","parent_team_id":null,"next_node":{"display_name":"child","wire-name":"child","day_value":"Tuesday","custom_value":"43","team_id":"6ba7b811-9dad-11d1-80b4-00c04fd430c8","parent_team_id":null}}}""",
            Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("nested", payload.RootElement.GetProperty("display_name").GetString());
        Assert.Equal("42", payload.RootElement.GetProperty("custom_value").GetString());
        using var stream = JsonDocument.Parse(await client.GetStringAsync("/contract-stream"));
        Assert.Equal("stream", stream.RootElement[0].GetProperty("display_name").GetString());
        using var ordinary = JsonDocument.Parse(await client.GetStringAsync("/ordinary-contract"));
        Assert.Equal("ordinary", ordinary.RootElement.GetProperty("displayName").GetString());

        foreach (var format in new[] { "json", "yml" })
        {
            var settings = new OpenApiReaderSettings();
            settings.AddYamlReader();
            var parsed = OpenApiDocument.Parse(await client.GetStringAsync("/openapi/v1." + format),
                format == "yml" ? "yaml" : "json", settings);
            Assert.Empty(parsed.Diagnostic!.Errors);
            var document = parsed.Document!;
            var operation = document.Paths!["/contract"].Operations![HttpMethod.Post];
            Assert.Equal(JsonSchemaType.String, operation.Responses!["400"].Content!["text/plain"].Schema!.Type);
            var result = operation.Responses!["200"].Content!["application/json"].Schema!;
            var body = operation.RequestBody!.Content!["application/json"].Schema!.Properties!["payload"];
            var item =
                document.Paths["/contract-stream"].Operations![HttpMethod.Get].Responses!["200"].Content![
                    "application/json"].Schema!.Items!;
            foreach (var schema in new[] { result, body, item })
            {
                Assert.Contains("display_name", schema.Properties!.Keys);
                Assert.Contains("wire-name", schema.Properties.Keys);
                Assert.Contains("display_name", schema.Required!);
                Assert.Contains(schema.Properties["day_value"].Enum!, value => value!.GetValue<string>() == "Monday");
                Assert.Null(schema.Properties["custom_value"].Type);
                Assert.Equal(JsonSchemaType.String, schema.Properties["team_id"].Type);
                Assert.Equal("uuid", schema.Properties["team_id"].Format);
                Assert.True(schema.Properties["parent_team_id"].Type!.Value.HasFlag(JsonSchemaType.String));
                Assert.True(schema.Properties["parent_team_id"].Type!.Value.HasFlag(JsonSchemaType.Null));
                Assert.Equal("uuid", schema.Properties["parent_team_id"].Format);
                var child = schema.Properties["next_node"];
                Assert.True(child.Type!.Value.HasFlag(JsonSchemaType.Null));
                Assert.Contains("display_name", child.Properties!.Keys);
                Assert.Contains("display_name", child.Properties["next_node"].Properties!.Keys);
            }

            var appSchema =
                document.Paths["/ordinary-contract"].Operations![HttpMethod.Get].Responses!["200"].Content![
                    "application/json"].Schema!;
            Assert.Contains("displayName", appSchema.Properties!.Keys);
            Assert.DoesNotContain("display_name", appSchema.Properties.Keys);
        }
    }

    /// <summary>Both zero-configuration routes serve equivalent valid OpenAPI 3.1 documents.</summary>
    [Fact]
    public async Task ShouldServeEquivalentOpenApi31DocumentsGivenPortiaApplicationWhenJsonAndYamlAreRequested()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        _ = builder.Services.AddPortia().AddHttp();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _ = builder.Services.AddAntiforgery();
        _app = builder.Build();
        _ = _app.MapPortiaOpenApi();

        var group = _app.MapGroup("/api").WithTags("orders");
        _ = group.MapPortiaPost<HttpCreateOrder, Uuid>("/orders")
            .WithSummary("Create an order")
            .WithDescription("Creates one order.");
        _ = group.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}", endpoint => endpoint
            .Parameter<Uuid>("widget_id", PortiaHttpParameterLocation.Route)
            .Parameter<bool>("archived", PortiaHttpParameterLocation.Header, required: false)
            .OnBind(http => new HttpGetWidget(
                Uuid.Parse((string)http.Request.RouteValues["widget_id"]!, CultureInfo.InvariantCulture), false))
            .Produces(StatusCodes.Status302Found)
            .OnResult((_, result) => result.IsSuccess ? Results.Redirect("/widgets/current") : null));
        _ = group.MapPortiaPost<HttpOptionalBody, string>("/upload", endpoint => endpoint
            .Accepts<Stream>("application/octet-stream")
            .OnBind(_ => new HttpOptionalBody())
            .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream")
            .OnResult((_, result) => result.IsSuccess ? Results.Stream(Stream.Null) : null));
        _ = group.MapPortiaPost<HttpUpdateWidget, string>("/widgets/{widget_id}/update", endpoint => endpoint
            .FromQuery(x => x.DryRun));
        _ = group.MapPortiaPost<HttpSendPing>("/ping");
        _ = group.MapPortiaPost<HttpGuardedQueueAction>("/guarded-queue", endpoint => endpoint
            .Produces(StatusCodes.Status302Found)
            .OnResult((_, result) => result.IsSuccess ? Results.Redirect("/done") : null));
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
        var widgetId = Assert.Single(get.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "widget_id" &&
            parameter.GetProperty("in").GetString() == "path");
        Assert.Equal("string", widgetId.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("uuid", widgetId.GetProperty("schema").GetProperty("format").GetString());
        Assert.Contains(get.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "archived" &&
            parameter.GetProperty("in").GetString() == "header" &&
            (!parameter.TryGetProperty("required", out var required) || !required.GetBoolean()));
        var getResponses = get.GetProperty("responses");
        Assert.True(getResponses.TryGetProperty("302", out _));
        Assert.False(getResponses.TryGetProperty("200", out _));
        var createContent = create.GetProperty("requestBody").GetProperty("content");
        Assert.False(createContent.TryGetProperty("application/x-www-form-urlencoded", out _));
        var updateContent = paths.GetProperty("/api/widgets/{widget_id}/update").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content");
        foreach (var mediaType in new[] { "application/json", "application/x-www-form-urlencoded", "multipart/form-data" })
        {
            var schema = updateContent.GetProperty(mediaType).GetProperty("schema");
            var properties = schema.GetProperty("properties");
            Assert.True(properties.TryGetProperty("quantity", out _), mediaType);
            Assert.False(properties.TryGetProperty("dry_run", out _), mediaType);
            var required = schema.GetProperty("required").EnumerateArray()
                .Select(item => item.GetString()).ToArray();
            Assert.Contains("name", required);
            Assert.Contains("quantity", required);
            if (mediaType == "application/json")
                Assert.Contains("active", required);
            else
                Assert.DoesNotContain("active", required);
        }
        var dryRun = Assert.Single(paths.GetProperty("/api/widgets/{widget_id}/update").GetProperty("post")
            .GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "dry_run");
        Assert.Equal("query", dryRun.GetProperty("in").GetString());
        Assert.False(dryRun.TryGetProperty("required", out var dryRunRequired) && dryRunRequired.GetBoolean());
        Assert.Equal("boolean", dryRun.GetProperty("schema").GetProperty("type").GetString());
        var uploadOperation = paths.GetProperty("/api/upload").GetProperty("post");
        var upload = uploadOperation.GetProperty("requestBody");
        var binary = upload.GetProperty("content").GetProperty("application/octet-stream").GetProperty("schema");
        Assert.Equal("string", binary.GetProperty("type").GetString());
        Assert.Equal("binary", binary.GetProperty("format").GetString());
        var binaryResponse = uploadOperation.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/octet-stream").GetProperty("schema");
        Assert.Equal("string", binaryResponse.GetProperty("type").GetString());
        Assert.Equal("binary", binaryResponse.GetProperty("format").GetString());
        Assert.True(paths.GetProperty("/api/ping").GetProperty("post").GetProperty("responses")
            .TryGetProperty("202", out _));
        var guardedQueue = paths.GetProperty("/api/guarded-queue").GetProperty("post");
        Assert.False(guardedQueue.GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(guardedQueue.GetProperty("responses").TryGetProperty("302", out _));
        Assert.DoesNotContain(guardedQueue.TryGetProperty("parameters", out var guardedParameters)
            ? guardedParameters.EnumerateArray().ToArray()
            : [], parameter => parameter.GetProperty("name").GetString() == "Prefer");
        var responses = create.GetProperty("responses");
        var createdId = responses.GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        Assert.Equal("string", createdId.GetProperty("type").GetString());
        Assert.Equal("uuid", createdId.GetProperty("format").GetString());
        var problemSchema = responses.GetProperty("400").GetProperty("content")
            .GetProperty("application/problem+json").GetProperty("schema");
        Assert.True(problemSchema.GetProperty("properties").TryGetProperty("status", out _));
        Assert.True(problemSchema.GetProperty("properties").TryGetProperty("detail", out _));
        Assert.False(problemSchema.GetProperty("properties").TryGetProperty("message", out _));
        Assert.True(responses.GetProperty("400").GetProperty("headers")
            .TryGetProperty(ResultHttpExtensions.TransientHeaderName, out _));
        Assert.True(responses.GetProperty("415").GetProperty("content")
            .TryGetProperty("application/problem+json", out _));
        Assert.True(responses.GetProperty("401").GetProperty("headers").TryGetProperty("WWW-Authenticate", out _));
        Assert.True(paths.TryGetProperty("/health", out _));
        Assert.False(paths.TryGetProperty("/api/widget-events", out _));
    }

    /// <summary>
    ///     Registering HTTP services does not publish the document unless the application maps it.
    /// </summary>
    [Fact]
    public async Task ShouldNotMapOpenApiEndpointsUntilExplicitlyRequested()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        _ = builder.Services.AddPortia().AddHttp();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _app = builder.Build();
        _ = _app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");
        await _app.StartAsync();

        using var client = _app.GetTestClient();
        var json = await client.GetAsync("/openapi/v1.json");
        var yaml = await client.GetAsync("/openapi/v1.yml");
        var widget = await client.GetAsync($"/widgets/{Uuid.CreateVersion4()}");

        Assert.Equal(HttpStatusCode.NotFound, json.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, yaml.StatusCode);
        // Publishing the document is independent from whether the application runs.
        Assert.Equal(HttpStatusCode.OK, widget.StatusCode);
    }

    /// <summary>
    ///     Explicit HTTP activation composes the document, so an application can
    ///     map it where and how it wants — behind authorization, on an internal path, or on a
    ///     separate port — and still get Portia's operation IDs, parameters and responses.
    /// </summary>
    [Fact]
    public async Task ShouldLetApplicationMapTheDocumentOnItsOwnProtectedRoute()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        _ = builder.Services.AddPortia().AddHttp();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _app = builder.Build();
        _ = _app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");
        _ = _app.MapOpenApi("/internal/openapi/{documentName}.json");
        await _app.StartAsync();

        using var client = _app.GetTestClient();
        var response = await client.GetAsync("/internal/openapi/v1.json");
        var document = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var raw = JsonDocument.Parse(document);
        Assert.Equal("3.1.1", raw.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("httpGetWidget", raw.RootElement.GetProperty("paths")
            .GetProperty("/widgets/{widget_id}").GetProperty("get").GetProperty("operationId").GetString());
    }

    /// <summary>
    ///     Without an antiforgery service, default binding rejects form posts, so the document describes
    ///     only JSON for those bodies; an endpoint that disables antiforgery still describes form content.
    /// </summary>
    [Fact]
    public async Task ShouldDescribeFormContentOnlyWhenFormPostsAreAccepted()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        _ = builder.Services.AddPortia().AddHttp();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _app = builder.Build();
        _ = _app.MapPortiaOpenApi();
        _ = _app.MapPortiaPost<HttpOptionalBody, string>("/unguarded");
        _ = _app.MapPortiaPost<HttpUpdateWidget, string>("/opted-out/{widget_id}").DisableAntiforgery();
        await _app.StartAsync();

        var json = await _app.GetTestClient().GetStringAsync("/openapi/v1.json");

        using var raw = JsonDocument.Parse(json);
        var paths = raw.RootElement.GetProperty("paths");
        var unguarded = paths.GetProperty("/unguarded").GetProperty("post").GetProperty("requestBody")
            .GetProperty("content");
        var optedOut = paths.GetProperty("/opted-out/{widget_id}").GetProperty("post").GetProperty("requestBody")
            .GetProperty("content");
        Assert.True(unguarded.TryGetProperty("application/json", out _), json);
        Assert.False(unguarded.TryGetProperty("application/x-www-form-urlencoded", out _), json);
        Assert.False(unguarded.TryGetProperty("multipart/form-data", out _), json);
        Assert.True(optedOut.TryGetProperty("application/x-www-form-urlencoded", out _), json);
        Assert.True(optedOut.TryGetProperty("multipart/form-data", out _), json);
    }

    /// <summary>Cross-generator operation ID collisions fail document generation deterministically.</summary>
    [Fact]
    public async Task ShouldKeepApplicationRunningGivenCollisionWhenOpenApiDocumentFails()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        _ = builder.Services.AddPortia().AddHttp();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _app = builder.Build();
        _ = _app.MapPortiaOpenApi();
        _ = _app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}");
        _ = _app.MapGet("/ordinary", () => Results.Ok()).WithName("httpGetWidget");
        await _app.StartAsync();

        var exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _app.GetTestClient().GetAsync("/openapi/v1.json"));
        Assert.Contains("operationId 'httpGetWidget' is duplicated", exception.Message, StringComparison.Ordinal);
    }
}

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cntryl.Portia.ReflectionDisabled;

public sealed class PutBodyCaseTests
{
    [Theory]
    [InlineData("""{"mode":"rename","name":"A"}""", HttpStatusCode.NoContent)]
    [InlineData("""{"mode":"archive"}""", HttpStatusCode.NoContent)]
    [InlineData("""{"mode":"rename"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"mode":"rename","mode":"archive","name":"A"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"name":"A"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"mode":5}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"mode":"unknown"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"mode":"rename" """, HttpStatusCode.BadRequest)]
    [InlineData("""{"mode":"rename","Mode":"archive","name":"A"}""", HttpStatusCode.BadRequest)]
    public async Task ShouldDispatchOneTypedCaseGivenValidSelector(string json, HttpStatusCode expected)
    {
        // Arrange
        var probe = new HandlingProbe();
        await using var app = ConfigureHost(probe, allowArchive: true);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var body = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        using var response = await client.PutAsync("/assets/1", body, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, response.StatusCode);

        if (expected == HttpStatusCode.NoContent)
            Assert.Single(probe.Handled);
        else
        {
            Assert.Empty(probe.Handled);
            Assert.Empty(probe.Evaluated);
        }
    }

    [Fact]
    public async Task ShouldEvaluateSelectedPermissionGivenCallerWithRenameOnly()
    {
        // Arrange
        var probe = new HandlingProbe();
        await using var app = ConfigureHost(probe, allowArchive: false);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Asset-Trace", "asset-trace");
        using var rename = new StringContent("""{"mode":"rename","name":"A"}""", Encoding.UTF8, "application/json");
        using var archive = new StringContent("""{"mode":"archive"}""", Encoding.UTF8, "application/json");

        // Act
        using var permitted = await client.PutAsync("/assets/1?revision=2", rename, TestContext.Current.CancellationToken);
        using var denied = await client.PutAsync("/assets/1?revision=2", archive, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, permitted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("rename:1:2:asset-trace", Assert.Single(probe.Handled));
        Assert.Equal(["assets:rename", "assets:archive"], probe.Evaluated);
    }

    [Fact]
    public async Task ShouldDescribeReferencedCasesAndCommonCredentialsGivenTypedPut()
    {
        // Arrange
        await using var app = ConfigureHost(new HandlingProbe(), allowArchive: false);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        // Act
        var json = await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        // Assert
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty("/assets/{id}").GetProperty("put");
        Assert.Equal("updateAsset", operation.GetProperty("operationId").GetString());
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("test", out _));
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToDictionary(
            parameter => parameter.GetProperty("name").GetString()!, parameter => parameter.GetProperty("in").GetString(),
            StringComparer.Ordinal);
        Assert.Equal("path", parameters["id"]);
        Assert.Equal("query", parameters["revision"]);
        Assert.Equal("header", parameters["X-Asset-Trace"]);
        var schema = operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("mode", schema.GetProperty("discriminator").GetProperty("propertyName").GetString());
        var components = root.GetProperty("components").GetProperty("schemas");
        var mapping = schema.GetProperty("discriminator").GetProperty("mapping");
        var references = schema.GetProperty("oneOf").EnumerateArray()
            .Select(choice => choice.GetProperty("$ref").GetString()).ToArray();
        Assert.Equal(2, references.Length);

        foreach (var name in new[] { "rename", "archive" })
        {
            var pointer = mapping.GetProperty(name).GetString()!;
            Assert.Contains(pointer, references);
            var branch = components.GetProperty(pointer.Split('/')[^1]);
            Assert.Contains("mode", branch.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(name, branch.GetProperty("properties").GetProperty("mode").GetProperty("const").GetString());
            Assert.Equal("assets:" + name, branch.GetProperty("x-portia-permission").GetString());
        }
    }

    [Fact]
    public async Task ShouldReturnSharedJsonPayloadGivenTypedResultCase()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ReadRevisionHandler>()
            .AddRequestHandler<PreviewRevisionHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaPutByBody<int>("/assets/{id}/revision", "assetRevision", "mode")
            .Case<ReadRevision>("").Case<PreviewRevision>("preview");
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var body = new StringContent("""{"mode":"preview","additional":1}""", Encoding.UTF8, "application/json");
        using var empty = new StringContent("""{"mode":""}""", Encoding.UTF8, "application/json");
        using var missing = new StringContent("""{"mode":"preview"}""", Encoding.UTF8, "application/json");

        // Act
        using var response = await client.PutAsync("/assets/4/revision", body, TestContext.Current.CancellationToken);
        using var emptySelection = await client.PutAsync("/assets/4/revision", empty, TestContext.Current.CancellationToken);
        using var failure = await client.PutAsync("/assets/4/revision", missing, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("5", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, emptySelection.StatusCode);
        Assert.Equal("4", await emptySelection.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShouldRejectOpenApiGivenDifferentCaseCredentials()
    {
        // Arrange
        await using var app = ConfigureHost(new HandlingProbe(), allowArchive: false, archiveCredential: "alternate");
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        // Act
        var exception = await Record.ExceptionAsync(() => client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("same OpenAPI credentials", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
    }

    static WebApplication ConfigureHost(HandlingProbe probe, bool allowArchive, string archiveCredential = "test")
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddSingleton(probe);
        _ = builder.Services.AddSingleton<IPermissionEvaluator, AssetPermissions>();
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApi.SecuritySchemes["test"] = new OpenApiSecurityScheme
            { Type = SecuritySchemeType.Http, Scheme = "bearer" };
            options.OpenApi.SecuritySchemes["alternate"] = new OpenApiSecurityScheme
            { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Alternate" };
            _ = options.OpenApi.SecurityFor<RenameAsset>("test").SecurityFor<ArchiveAsset>(archiveCredential);
            _ = options.FromHeader<IAssetUpdate>(request => request.Trace, "X-Asset-Trace");
        }).AddRequestHandler<RenameAssetHandler>().AddRequestHandler<ArchiveAssetHandler>();
        var app = builder.Build();
        app.Use((context, next) =>
        {
            var claims = new List<Claim> { new("permission", "assets:rename") };

            if (allowArchive)
                claims.Add(new Claim("permission", "assets:archive"));

            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
            return next(context);
        });
        _ = app.MapPortiaPutByBody("/assets/{id}", "updateAsset", "mode", "revision")
            .Case<RenameAsset>("rename").Case<ArchiveAsset>("archive");
        _ = app.MapPortiaOpenApi();
        return app;
    }
}

interface IAssetUpdate
{
    string? Trace { get; }
}

[RequiresPermission("assets:rename")]
sealed record RenameAsset(int Id, string Mode, string Name, int Revision = 0, string? Trace = null)
    : IRequest, IAssetUpdate;
[RequiresPermission("assets:archive")]
sealed record ArchiveAsset(int Id, string Mode, int Revision = 0, string? Trace = null) : IRequest, IAssetUpdate;

sealed class HandlingProbe
{
    public List<string> Handled { get; } = [];

    public List<string> Evaluated { get; } = [];
}

sealed class AssetPermissions(HandlingProbe probe) : IPermissionEvaluator
{
    public ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default)
    {
        probe.Evaluated.Add(permission);
        return ValueTask.FromResult(actor.HasClaim("permission", permission)
            ? Result.Success
            : Result.Failure(RequestError.Forbidden("Missing case permission.")));
    }
}

sealed class RenameAssetHandler(HandlingProbe probe) : IRequestHandler<RenameAsset>
{
    public ValueTask<Result> HandleAsync(IRequestContext<RenameAsset> context, CancellationToken ct)
    {
        probe.Handled.Add(FormattableString.Invariant($"rename:{context.Request.Id}:{context.Request.Revision}:{context.Request.Trace}"));
        return ValueTask.FromResult(Result.Success);
    }
}

sealed class ArchiveAssetHandler(HandlingProbe probe) : IRequestHandler<ArchiveAsset>
{
    public ValueTask<Result> HandleAsync(IRequestContext<ArchiveAsset> context, CancellationToken ct)
    {
        probe.Handled.Add("archive");
        return ValueTask.FromResult(Result.Success);
    }
}

sealed record ReadRevision(int Id) : IRequest<int>;
sealed record PreviewRevision(int Id, string Mode,
    [RequiredWhen(nameof(PreviewRevision.Mode), "preview")] int? Additional = null) : IRequest<int>;

sealed class ReadRevisionHandler : IRequestHandler<ReadRevision, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<ReadRevision> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(context.Request.Id));
}

sealed class PreviewRevisionHandler : IRequestHandler<PreviewRevision, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<PreviewRevision> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(context.Request.Id + context.Request.Additional.GetValueOrDefault()));
}

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(RenameAsset))]
[JsonSerializable(typeof(ArchiveAsset))]
[JsonSerializable(typeof(ReadRevision))]
[JsonSerializable(typeof(PreviewRevision))]
sealed partial class PutBodyCaseJsonContext : JsonSerializerContext;

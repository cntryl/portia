using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

/// <summary>Member facts remain local and reflect effective binding defaults.</summary>
public sealed class ContractMemberMetadataTests
{
    /// <summary>Component reuse must not reuse another member's title.</summary>
    [Fact]
    public async Task ShouldKeepTitlesLocalGivenSharedObjectType()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ContractMemberTitleHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<ContractFirstTitleRequest, ContractMetadataModel>("/first-title");
        _ = app.MapPortiaPost<ContractSecondTitleRequest, ContractMetadataModel>("/second-title");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).Document!;

        // Assert
        var first =
            document.Paths["/first-title"].Operations![HttpMethod.Post].RequestBody!.Content!["application/json"]
                .Schema!.Properties!["payload"];
        var second =
            document.Paths["/second-title"].Operations![HttpMethod.Post].RequestBody!.Content!["application/json"]
                .Schema!.Properties!["payload"];
        Assert.Equal("First payload", first.Title);
        Assert.Equal("Second payload", second.Title);
        Assert.Equal(Assert.IsType<OpenApiSchemaReference>(first).Reference.Id,
            Assert.IsType<OpenApiSchemaReference>(second).Reference.Id);
    }

    /// <summary>A valid literal initializer permits omission despite a Required value rule.</summary>
    [Fact]
    public async Task ShouldPermitOmissionGivenValidLiteralPropertyDefault()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ContractLiteralDefaultHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<ContractLiteralDefaultRequest, string>("/literal-default");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).Document!;

        // Assert
        var schema =
            document.Paths["/literal-default"].Operations![HttpMethod.Post].RequestBody!.Content!["application/json"]
                .Schema!.Properties!["payload"];
        Assert.DoesNotContain("name", schema.Required?.ToArray() ?? []);
        Assert.Equal("fallback", schema.Properties!["name"].Default!.GetValue<string>());
    }

    /// <summary>Query requirements follow value validation and do not inherit JSON-only presence rules.</summary>
    [Fact]
    public async Task ShouldRequireQueryValueGivenRequiredWithNullDefault()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ContractQueryRulesHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractQueryRulesRequest, string>("/query-rules");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).Document!;

        // Assert
        var parameters = document.Paths["/query-rules"].Operations![HttpMethod.Get].Parameters!;
        Assert.True(parameters.Single(parameter => parameter.Name == "name").Required);
        Assert.False(parameters.Single(parameter => parameter.Name == "marker").Required);
    }

    /// <summary>The JSON body does not advertise members the binder ignores while reading.</summary>
    [Fact]
    public async Task ShouldOmitJsonInputGivenReadIgnoreAttribute()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ContractIgnoredInputHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<ContractIgnoredInputRequest, string>("/ignored-input");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).Document!;

        // Assert
        var schema =
            document.Paths["/ignored-input"].Operations![HttpMethod.Post].RequestBody!.Content!["application/json"]
                .Schema!;
        Assert.False(schema.Properties?.ContainsKey("server_name") == true);
    }
}

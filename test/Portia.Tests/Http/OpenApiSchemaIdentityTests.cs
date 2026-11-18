using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cntryl.Portia;

/// <summary>Stable readable component identities for API clients.</summary>
public sealed class OpenApiSchemaIdentityTests
{
    /// <summary>Editorial changes preserve component identities and generated client type names.</summary>
    [Fact]
    public async Task ShouldPreserveComponentIdentityGivenDescriptionEdit()
    {
        // Arrange
        await using var before = await CreateAppAsync("Original description.");
        await using var after = await CreateAppAsync("A clearer description.");

        // Act
        var beforeSchema = await ReadResultSchemaAsync(before);
        var afterSchema = await ReadResultSchemaAsync(after);

        // Assert
        Assert.Equal(beforeSchema.Reference.Id, afterSchema.Reference.Id);
        Assert.Contains(nameof(OpenApiDescribedNode), beforeSchema.Reference.Id, StringComparison.Ordinal);
        Assert.Equal("Original description.", beforeSchema.Description);
        Assert.Equal("A clearer description.", afterSchema.Description);
    }

    static async Task<WebApplication> CreateAppAsync(string description)
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp(options =>
                options.OpenApi.DescribeSchema<OpenApiDescribedNode>(description))
            .AddRequestHandler<OpenApiDescribedHandler>();
        var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiDescribedRequest, OpenApiDescribedNode>("/identity");
        await app.StartAsync();
        return app;
    }

    static async Task<OpenApiSchemaReference> ReadResultSchemaAsync(WebApplication app)
    {
        using var client = app.GetTestClient();
        var parsed = OpenApiDocument.Parse(await client.GetStringAsync("/openapi/v1.json"), "json",
            new OpenApiReaderSettings());
        Assert.Empty(parsed.Diagnostic!.Errors);
        return Assert.IsType<OpenApiSchemaReference>(parsed.Document!.Paths["/identity"].Operations![HttpMethod.Post]
            .Responses!["200"].Content!["application/json"].Schema);
    }
}

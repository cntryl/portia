using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

/// <summary>Value validation and JSON presence are separate contracts.</summary>
public sealed class RequiredPresenceTests
{
    /// <summary>JSON presence rejects omission while nullable values and valid defaults remain legal.</summary>
    [Theory]
    [InlineData("{}", HttpStatusCode.BadRequest)]
    [InlineData("{\"marker\":null}", HttpStatusCode.OK)]
    [InlineData("{\"marker\":null,\"name\":null}", HttpStatusCode.BadRequest)]
    [InlineData("{\"marker\":null,\"name\":\"  \"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"marker\":null,\"count\":0}", HttpStatusCode.OK)]
    public async Task ShouldRespectPresenceAndValueRulesGivenJsonInput(string json, HttpStatusCode status)
    {
        // Arrange
        await using var app = await CreateAsync();
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var client = app.GetTestClient();

        // Act
        using var response = await client.PostAsync("/presence", body);

        // Assert
        Assert.Equal(status, response.StatusCode);
    }

    /// <summary>The schema requires JSON presence independently of nullability and valid defaults.</summary>
    [Fact]
    public async Task ShouldDocumentPresenceSeparatelyGivenRequiredValueWithValidDefault()
    {
        // Arrange
        await using var app = await CreateAsync();
        using var client = app.GetTestClient();

        // Act
        var parsed = OpenApiDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        // Assert
        Assert.Empty(parsed.Diagnostic!.Errors);
        var schema =
            parsed.Document!.Paths["/presence"].Operations![HttpMethod.Post].RequestBody!.Content!["application/json"]
                .Schema!;
        Assert.Contains("marker", schema.Required!);
        Assert.DoesNotContain("name", schema.Required!);
        Assert.True(schema.Properties!["marker"].Type!.Value.HasFlag(JsonSchemaType.Null));
        Assert.False(schema.Properties["name"].Type!.Value.HasFlag(JsonSchemaType.Null));
        Assert.Equal(1, schema.Properties["name"].MinLength);
    }

    /// <summary>
    ///     A member that JSON requires can be redirected to a header: the body omits it, and only a
    ///     body copy is refused.
    /// </summary>
    [Theory]
    [InlineData("{}", HttpStatusCode.OK)]
    [InlineData("{\"marker\":\"body\"}", HttpStatusCode.BadRequest)]
    public async Task ShouldBindFromHeaderGivenJsonRequiredMember(string json, HttpStatusCode status)
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<RequiredPresenceHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaPost<RequiredPresenceRequest, string>("/presence-header", configuration =>
            configuration.FromHeader(request => request.Marker, "Marker"));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/presence-header")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Marker", "header");

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(status, response.StatusCode);
    }

    /// <summary>
    ///     A header-bound member whose default fails its own validation must be sent, and OpenAPI says
    ///     so exactly as it does for a query parameter.
    /// </summary>
    [Fact]
    public async Task ShouldRequireHeaderParameterGivenDefaultFailsValidation()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<RequiredHeaderPresenceHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<RequiredHeaderPresenceRequest, string>("/header-presence", configuration =>
            configuration.FromHeader(request => request.Region, "Region"));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var missing = await client.GetAsync("/header-presence");

        // Act
        var parsed = OpenApiDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var parameter = Assert.Single(parsed.Document!.Paths["/header-presence"].Operations![HttpMethod.Get]
            .Parameters!);
        Assert.Equal(ParameterLocation.Header, parameter.In);
        Assert.True(parameter.Required);
    }

    static async Task<WebApplication> CreateAsync()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<RequiredPresenceHandler>();
        var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<RequiredPresenceRequest, string>("/presence");
        await app.StartAsync();
        return app;
    }
}

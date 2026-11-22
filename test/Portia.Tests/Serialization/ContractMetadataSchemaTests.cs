using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cntryl.Portia;

/// <summary>Shared schema facts describe standard model annotations.</summary>
public sealed class ContractMetadataSchemaTests
{
    /// <summary>Constraints apply to the same values validated during dispatch.</summary>
    [Fact]
    public async Task ShouldPublishConstraintsGivenStandardAttributes()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/metadata");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var text = await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var document = OpenApiDocument.Parse(text).Document!;
        var schema =
            document.Paths["/metadata"].Operations![HttpMethod.Get].Responses!["200"].Content!["application/json"]
                .Schema!;

        // Assert
        Assert.Equal(1, schema.Properties!["name"].MinLength);
        Assert.Equal(12, schema.Properties["name"].MaxLength);
        Assert.DoesNotContain("null", schema.Properties["name"].Type.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1", schema.Properties["limit"].ExclusiveMinimum);
        Assert.Equal("10", schema.Properties["limit"].Maximum);
        Assert.Equal(1, schema.Properties["items"].MinItems);
        Assert.Equal(3, schema.Properties["items"].MaxItems);
        Assert.Null(schema.Properties["items"].Items!.MinLength);
        Assert.Null(schema.Properties["items"].Items!.MaxLength);
        Assert.Equal("email", schema.Properties["email"].Format);
        Assert.Equal("uri", schema.Properties["website"].Format);
        Assert.NotNull(schema.Properties["code"].Pattern);
        Assert.Equal(5, schema.Properties["page_size"].Default!.GetValue<int>());
    }

    /// <summary>Input and output schemas follow serialization direction independently.</summary>
    [Fact]
    public async Task ShouldProjectDirectionGivenSerializerIgnoreConditions()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ContractMetadataInputHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<ContractMetadataInputRequest, ContractMetadataModel>("/direction");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).Document!;

        // Assert
        var operation = document.Paths["/direction"].Operations![HttpMethod.Post];
        var input = operation.RequestBody!.Content!["application/json"].Schema!.Properties!["payload"];
        var output = operation.Responses!["200"].Content!["application/json"].Schema!;
        Assert.DoesNotContain("server_value", input.Properties!.Keys);
        Assert.Contains("secret", input.Properties.Keys);
        Assert.DoesNotContain("secret", output.Properties!.Keys);
        Assert.Contains("server_value", output.Properties.Keys);
    }
}

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

/// <summary>Host composition enriches HTTP contracts without annotating models with ASP.NET types.</summary>
public sealed class OpenApiEnrichmentTests
{
    /// <summary>Standard endpoint organization and application document metadata survive generation.</summary>
    [Fact]
    public async Task ShouldPublishOrganizationGivenHostMetadata()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp(options => options.OpenApi.ConfigureDocument = document =>
        {
            document.Info.Title = "Contract API";
            document.Info.Version = "0.14.1";
            document.Info.Description = "**Application** documentation.";
            document.Info.Contact = new OpenApiContact { Name = "Support", Email = "support@example.com" };
            document.Info.License = new OpenApiLicense { Name = "MIT", Identifier = "MIT" };
            document.Info.TermsOfService = new Uri("https://example.com/terms");
            document.Servers = [new OpenApiServer { Url = "https://api.example.com" }];
            document.ExternalDocs = new OpenApiExternalDocs { Url = new Uri("https://example.com/guide") };
        }).AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/organized")
            .WithName("getContract").WithTags("Contracts").WithSummary("Get a contract")
            .WithDescription("Returns a **documented** contract.");
        await app.StartAsync();

        // Act
        var parsed = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json"));

        // Assert
        Assert.Empty(parsed.Diagnostic!.Errors);
        var document = parsed.Document!;
        var operation = document.Paths["/organized"].Operations![HttpMethod.Get];
        Assert.Equal("getContract", operation.OperationId);
        Assert.Equal("Get a contract", operation.Summary);
        Assert.Contains("**documented**", operation.Description, StringComparison.Ordinal);
        Assert.Equal("Contracts", Assert.Single(operation.Tags!).Name);
        Assert.Equal("Contract API", document.Info.Title);
        Assert.Equal("0.14.1", document.Info.Version);
        Assert.Equal("support@example.com", document.Info.Contact!.Email);
        Assert.Equal("MIT", document.Info.License!.Identifier);
        Assert.Equal("https://api.example.com", Assert.Single(document.Servers!).Url);
        Assert.Equal("https://example.com/guide", document.ExternalDocs!.Url!.ToString());
    }

    /// <summary>Named typed examples use actual serialization and stay local to their endpoint overrides.</summary>
    [Fact]
    public async Task ShouldPublishExamplesAndHeadersGivenTypedValues()
    {
        // Arrange
        var shared = Example("Shared");
        var endpoint = Example("Endpoint");
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddExample("normal", shared).AddHttp()
            .AddRequestHandler<ContractExampleHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<ContractExampleRequest, ContractMetadataModel>("/examples/{id}", configuration => configuration
            .FromQuery(request => request.PageSize)
            .RequestExample("create", new ContractExampleRequest(7, endpoint))
            .ResponseExample(200, "normal", endpoint)
            .ResponseHeader<string>(200, "ETag", "Version token.", required: true));
        await app.StartAsync();

        // Act
        var document = JsonNode.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json"))!;

        // Assert
        var operation = document["paths"]!["/examples/{id}"]!["post"]!;
        Assert.Equal("Create example", operation["requestBody"]!["content"]!["application/json"]!["schema"]!["title"]!.GetValue<string>());
        var input = operation["requestBody"]!["content"]!["application/json"]!["examples"]!["create"]!["value"]!;
        Assert.Equal("Endpoint", input["payload"]!["name"]!.GetValue<string>());
        Assert.Null(input["id"]);
        Assert.Null(input["page_size"]);
        var response = operation["responses"]!["200"]!;
        Assert.Equal("Endpoint", response["content"]!["application/json"]!["examples"]!["normal"]!["value"]!["name"]!.GetValue<string>());
        Assert.Equal("Version token.", response["headers"]!["ETag"]!["description"]!.GetValue<string>());
        Assert.True(response["headers"]!["ETag"]!["required"]!.GetValue<bool>());
    }

    /// <summary>Invalid examples fail document generation with their names and validation details.</summary>
    [Fact]
    public async Task ShouldRejectExampleGivenInvalidAnnotatedValue()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddExample("broken", Example(""))
            .AddHttp().AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/invalid-example");
        await app.StartAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => app.GetTestClient().GetStringAsync("/openapi/v1.json"));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("broken", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Name", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Shared examples follow a custom result hook's declared body rather than the handler result.</summary>
    [Fact]
    public async Task ShouldUseDeclaredResponseExampleGivenReplacedSuccessBody()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddExample("handler", Example("Handler"))
            .AddExample("actual", "actual response", use: PortiaContractUse.Output)
            .AddHttp().AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/custom-example", configuration => configuration
            .Produces<string>(200).OnResult((_, _) => Results.Json("actual response")));
        await app.StartAsync();

        // Act
        var document = JsonNode.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json"))!;

        // Assert
        var examples = document["paths"]!["/custom-example"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["examples"]!;
        Assert.Null(examples["handler"]);
        Assert.Equal("actual response", examples["actual"]!["value"]!.GetValue<string>());
    }

    /// <summary>Endpoint enrichment cannot mutate a shared error response used by another status.</summary>
    [Fact]
    public async Task ShouldIsolateResponseHeaderGivenSharedErrorComponent()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/header", configuration => configuration
            .ResponseHeader<string>(400, "X-Reason", "Rejection reason."));
        await app.StartAsync();

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json")).Document!;

        // Assert
        var responses = document.Paths["/header"].Operations![HttpMethod.Get].Responses!;
        Assert.True(responses["400"].Headers!.ContainsKey("X-Reason"));
        Assert.False(responses["409"].Headers!.ContainsKey("X-Reason"));
    }

    /// <summary>Route and query examples follow scalar parsing rather than a JSON member converter.</summary>
    [Fact]
    public async Task ShouldPublishScalarParametersGivenJsonMemberConverters()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ConvertedExampleHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<ConvertedExampleRequest, string>("/converted/{id}", configuration => configuration
            .FromQuery(request => request.Count).RequestExample("converted", new ConvertedExampleRequest(16, 7)));
        await app.StartAsync();

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json")).Document!;

        // Assert
        var parameters = document.Paths["/converted/{id}"].Operations![HttpMethod.Post].Parameters!;
        Assert.Equal(16, parameters.Single(parameter => parameter.Name == "id").Examples!["converted"].Value!.GetValue<int>());
        Assert.Equal(7, parameters.Single(parameter => parameter.Name == "count").Examples!["converted"].Value!.GetValue<int>());
    }

    /// <summary>Enum parameter examples retain the representation advertised by their scalar schema.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPublishEnumExampleGivenParameterRepresentation(bool strings)
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().ConfigureJson(options =>
        {
            if (strings)
                options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter<ExampleMode>(System.Text.Json.JsonNamingPolicy.CamelCase));
        }).AddHttp().AddRequestHandler<EnumExampleHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<EnumExampleRequest, string>("/enum-example", configuration => configuration
            .RequestExample("first", new EnumExampleRequest(ExampleMode.First)));
        await app.StartAsync();

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json")).Document!;

        // Assert
        var parameter = Assert.Single(document.Paths["/enum-example"].Operations![HttpMethod.Get].Parameters!);
        if (strings)
        {
            Assert.True(parameter.Schema!.Type is null or JsonSchemaType.String);
            Assert.Equal("first", parameter.Examples!["first"].Value!.GetValue<string>());
        }
        else
        {
            Assert.Equal(JsonSchemaType.Integer, parameter.Schema!.Type);
            Assert.Equal(1, parameter.Examples!["first"].Value!.GetValue<int>());
        }
    }

    static ContractMetadataModel Example(string name) => new(name, 5, ["one"], "AB", "hi@example.com", "https://example.com");
}

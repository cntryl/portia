using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cntryl.Portia;

/// <summary>Explicit OpenAPI description contracts.</summary>
public sealed class OpenApiDescriptionTests
{
    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Theory]
    [InlineData("json")]
    [InlineData("yml")]
    public async Task ShouldApplyDescriptionPrecedenceGivenSharedTypesAndEndpointOverrides(string format)
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApi.DescribeMember("limit", "Shared limit.");
            options.OpenApi.DescribeMember("wire-name", "Shared name.");
            options.OpenApi.DescribeMember("next", "Next node.");
            options.OpenApi.DescribeSchema<OpenApiDescribedRequest>("Request.", d => d
                .Property(x => x.Limit, "Type limit.").Property(x => x.Payload, "Type payload."));
            options.OpenApi.DescribeSchema<OpenApiOtherDescribedRequest>("Other request.",
                d => d.Property(x => x.Limit, "Type limit."));
            options.OpenApi.DescribeSchema<OpenApiDescribedNode>("A node.", d => d.Property(x => x.Name, "Type name."));
        }).AddRequestHandler<OpenApiDescribedHandler>().AddRequestHandler<OpenApiOtherDescribedHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiDescribedRequest, OpenApiDescribedNode>("/first", c => c
            .FromQuery(x => x.Limit).Describe(x => x.Limit, "Endpoint limit.")
            .Describe(x => x.Payload, "Endpoint payload.").DescribeResponse("A node response.")
            .DescribeResponse(409, "This endpoint's conflict."));
        _ = app.MapPortiaPost<OpenApiOtherDescribedRequest, OpenApiDescribedNode>("/second",
            c => c.FromQuery(x => x.Limit));
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, format);

        // Assert
        var first = document.Paths["/first"].Operations![HttpMethod.Post];
        var second = document.Paths["/second"].Operations![HttpMethod.Post];
        Assert.Equal("Endpoint limit.", Assert.Single(first.Parameters!).Description);
        Assert.Equal("Type limit.", Assert.Single(second.Parameters!).Description);
        Assert.Equal("A node response.", first.Responses!["200"].Description);
        Assert.Equal("This endpoint's conflict.", first.Responses["409"].Description);
        Assert.Equal("Error", second.Responses!["409"].Description);
        var body = first.RequestBody!.Content!["application/json"].Schema!;
        Assert.Equal("Request.", body.Description);
        Assert.Equal("Endpoint payload.", body.Properties!["payload"].Description);
        var result = first.Responses["200"].Content!["application/json"].Schema!;
        Assert.Equal("A node.", result.Description);
        Assert.Equal("Type name.", result.Properties!["wire-name"].Description);
        Assert.Equal("Next node.", result.Properties["next"].Description);
        Assert.Equal("Type name.", result.Properties["next"].Properties!["wire-name"].Description);
    }

    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Fact]
    public async Task ShouldReportAllDescriptionGapsGivenStrictCoverage()
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options => options.OpenApi.RequireDescriptions = true)
            .AddRequestHandler<OpenApiDescribedHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiDescribedRequest, OpenApiDescribedNode>("/missing",
            c => c.FromQuery(x => x.Limit));
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("POST /missing", failure.Message, StringComparison.Ordinal);
        Assert.Contains("parameter query limit", failure.Message, StringComparison.Ordinal);
        Assert.Contains("schema", failure.Message, StringComparison.Ordinal);
        Assert.Contains("property wire-name", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Fact]
    public async Task ShouldGenerateDocumentGivenCompleteDescriptions()
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApi.RequireDescriptions = true;
            options.OpenApi.DescribeSchema<OpenApiDescribedRequest>("Request.", d => d
                .Property(x => x.Limit, "Limit.").Property(x => x.Payload, "Payload."));
            options.OpenApi.DescribeSchema<OpenApiDescribedNode>("Node.", d => d
                .Property(x => x.Name, "Name.").Property(x => x.Next, "Next."));
        }).AddRequestHandler<OpenApiDescribedHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiDescribedRequest, OpenApiDescribedNode>("/complete",
                c => c.FromQuery(x => x.Limit))
            .WithSummary("Echo a node.");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, "json");

        // Assert
        Assert.Single(document.Paths);
    }

    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Fact]
    public async Task ShouldUseSharedDescriptionsGivenNoTypeOverrides()
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApi.DescribeMember("limit", "Shared limit.");
            options.OpenApi.DescribeMember("wire-name", "Shared name.");
        }).AddRequestHandler<OpenApiDescribedHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiDescribedRequest, OpenApiDescribedNode>("/shared", c => c.FromQuery(x => x.Limit));
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, "json");

        // Assert
        var operation = document.Paths["/shared"].Operations![HttpMethod.Post];
        Assert.Equal("Shared limit.", Assert.Single(operation.Parameters!).Description);
        Assert.Equal("Shared name.", operation.Responses!["200"].Content!["application/json"].Schema!
            .Properties!["wire-name"].Description);
    }

    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectResponseDescriptionGivenMissingOrAmbiguousStatus(bool ambiguous)
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<OpenApiDescribedHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiDescribedRequest, OpenApiDescribedNode>("/bad-response", c =>
        {
            if (ambiguous)
                _ = c.Produces(201).DescribeResponse("Which success?");
            else
                _ = c.DescribeResponse(418, "Not emitted.");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(ambiguous ? "needs a status code" : "does not emit described response '418'", failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Fact]
    public async Task ShouldDescribeDeclaredInputsAndStatusGivenCustomBindingAndResult()
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<OpenApiDescribedHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiDescribedRequest, OpenApiDescribedNode>("/custom", c => c
            .OnBind(_ => new OpenApiDescribedRequest(1, new OpenApiDescribedNode("node")))
            .Parameter<string>("X-Input", PortiaHttpParameterLocation.Header, description: "Custom input.")
            .Produces<OpenApiDescribedNode>(201).OnResult((_, _) => Results.StatusCode(201))
            .DescribeResponse("Created node."));
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, "json");

        // Assert
        var operation = document.Paths["/custom"].Operations![HttpMethod.Post];
        Assert.Equal("Custom input.", Assert.Single(operation.Parameters!).Description);
        Assert.Equal("Created node.", operation.Responses!["201"].Description);
        Assert.DoesNotContain("200", operation.Responses.Keys);
    }

    /// <summary>Converter representations retain the original contract's descriptions, including nullable values.</summary>
    [Fact]
    public async Task ShouldPreferContractDescriptionGivenNullableConverterMapping()
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApiSchemaType = type => type == typeof(OpenApiTraversalCount) ? typeof(long) : null;
            options.OpenApi.DescribeSchema<long>("Representation.");
            options.OpenApi.DescribeSchema<OpenApiTraversalCount>("Original contract.");
        }).AddRequestHandler<OpenApiNullableTraversalHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiNullableTraversalRequest, OpenApiTraversalCounts>("/mapped-descriptions");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, "json");

        // Assert
        var properties = document.Paths["/mapped-descriptions"].Operations![HttpMethod.Post].Responses!["200"]
            .Content!["application/json"].Schema!.Properties!;
        Assert.Equal("Original contract.", properties["required_count"].Description);
        Assert.Equal("Original contract.", properties["optional_count"].Description);
        Assert.True(properties["optional_count"].Type!.Value.HasFlag(JsonSchemaType.Null));
    }

    /// <summary>Nested dictionaries, converted arrays and polymorphic branches receive descriptions.</summary>
    [Fact]
    public async Task ShouldDescribeEveryBranchGivenPolymorphicConvertedCollections()
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApiSchemaType = type =>
                type == typeof(OpenApiTraversalCollection) ? typeof(OpenApiTraversalItem[]) : null;
            options.OpenApi.DescribeMember("kind", "Variant discriminator.");
            options.OpenApi.DescribeSchema<OpenApiTraversalCollection>("Collection.");
            options.OpenApi.DescribeSchema<OpenApiTraversalItem>("Item.",
                d => d.Property(x => x.ItemId, "Identifier."));
            options.OpenApi.DescribeSchema<OpenApiTraversalCollectionVariant>("Collection variant.");
            options.OpenApi.DescribeSchema<OpenApiTraversalTextVariant>("Text variant.");
        }).AddRequestHandler<OpenApiPolymorphicTraversalHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiPolymorphicTraversalRequest, OpenApiTraversalVariant>("/branches");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, "json");

        // Assert
        var branches = document.Paths["/branches"].Operations![HttpMethod.Post].Responses!["200"]
            .Content!["application/json"].Schema!.AnyOf!;
        var collection = Assert.Single(branches, branch => branch.Properties!.ContainsKey("items"));
        Assert.Equal("Collection variant.", collection.Description);
        Assert.Equal("Variant discriminator.", collection.Properties!["kind"].Description);
        var values = collection.Properties["groups"].AdditionalProperties!;
        Assert.Equal("Collection.", values.Description);
        Assert.Equal("Item.", values.Items!.Description);
        Assert.Equal("Identifier.", values.Items.Properties!["item_id"].Description);
        Assert.Equal("Text variant.",
            Assert.Single(branches, branch => branch.Properties!.ContainsKey("description")).Description);
    }

    /// <summary>Route and query parameters use their binding names while selectors retain CLR identity.</summary>
    [Fact]
    public async Task ShouldDescribeRouteAndQueryGivenTypedSelectors()
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<HttpGetWidgetHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<HttpGetWidget, string>("/described/{widget_id}", c => c
            .Describe(x => x.WidgetId, "Widget identifier.")
            .Describe(x => x.IncludeArchived, "Include archived widgets."));
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, "json");

        // Assert
        var parameters = document.Paths["/described/{widget_id}"].Operations![HttpMethod.Get].Parameters!;
        Assert.Equal("Widget identifier.", Assert.Single(parameters, p => p.Name == "widget_id").Description);
        Assert.Equal("Include archived widgets.",
            Assert.Single(parameters, p => p.Name == "include_archived").Description);
    }

    /// <summary>Standard model attributes describe parameters, schemas and recursive properties without registrations.</summary>
    [Theory]
    [InlineData("json")]
    [InlineData("yml")]
    public async Task ShouldUseStandardAttributesGivenNoDescriptionRegistry(string format)
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options => options.OpenApi.RequireDescriptions = true)
            .AddRequestHandler<OpenApiAnnotatedHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiAnnotatedRequest, OpenApiAnnotatedNode>("/attributes",
            c => c.FromQuery(x => x.Limit));
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, format);

        // Assert
        var operation = document.Paths["/attributes"].Operations![HttpMethod.Post];
        Assert.Equal("Attributed request.", operation.Description);
        Assert.Equal("Attributed limit.", Assert.Single(operation.Parameters!).Description);
        Assert.Equal("Attributed request.", operation.RequestBody!.Content!["application/json"].Schema!.Description);
        var node = operation.Responses!["200"].Content!["application/json"].Schema!;
        Assert.Equal("Attributed node.", node.Description);
        Assert.Equal("Attributed name.", node.Properties!["wire-name"].Description);
        Assert.Equal("Attributed next.", node.Properties["next"].Description);
    }

    /// <summary>Endpoint and application descriptions override attributes; shared defaults remain fallbacks.</summary>
    [Theory]
    [InlineData("shared", "Attributed limit.")]
    [InlineData("type", "Application limit.")]
    [InlineData("endpoint", "Endpoint limit.")]
    public async Task ShouldResolveDescriptionPrecedenceGivenStandardAttributes(string scope, string expected)
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApi.DescribeMember("limit", "Shared limit.");
            if (scope is "type" or "endpoint")
                options.OpenApi.DescribeSchema<OpenApiAnnotatedRequest>("Application request.",
                    d => d.Property(x => x.Limit, "Application limit."));
        }).AddRequestHandler<OpenApiAnnotatedHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiAnnotatedRequest, OpenApiAnnotatedNode>("/attribute-precedence", c =>
        {
            _ = c.FromQuery(x => x.Limit);
            if (scope == "endpoint")
                _ = c.Describe(x => x.Limit, "Endpoint limit.");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, "json");

        // Assert
        var operation = document.Paths["/attribute-precedence"].Operations![HttpMethod.Post];
        Assert.Equal(expected, Assert.Single(operation.Parameters!).Description);
    }

    /// <summary>Retained ASP.NET components use the same attribute precedence as Portia-generated schemas.</summary>
    [Fact]
    public async Task ShouldPreferAttributeOverSharedTextGivenRetainedAspNetSchema()
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options => options.OpenApi.DescribeMember("limit", "Shared limit."))
            .AddRequestHandler<OpenApiAnnotatedHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<OpenApiAnnotatedRequest, OpenApiAnnotatedNode>("/custom-attributes", c => c
            .Accepts<OpenApiAnnotatedRequest>("application/json")
            .OnBind(_ => new OpenApiAnnotatedRequest(1, new OpenApiAnnotatedNode("node"))));
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, "json");

        // Assert
        var schemas = document.Components!.Schemas!;
        var retained = Assert.Single(schemas, schema => !schema.Key.StartsWith("Portia_", StringComparison.Ordinal) &&
                                                        schema.Value.Properties?.ContainsKey("limit") == true).Value;
        Assert.Equal("Attributed limit.", retained.Properties!["limit"].Description);
    }

    /// <summary>Standard titles and deprecation metadata flow into the generated schema.</summary>
    [Fact]
    public async Task ShouldDescribeTitlesAndDeprecationGivenStandardAttributes()
    {
        // Arrange
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<OpenApiTitleHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<OpenApiTitleRequest, OpenApiTitleView>("/titles");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = await ReadAsync(app, "json");

        // Assert
        var schema =
            document.Paths["/titles"].Operations![HttpMethod.Get].Responses!["200"].Content!["application/json"]
                .Schema!;
        Assert.Equal("A titled view", schema.Title);
        Assert.Equal("Display label", schema.Properties!["label"].Title);
        Assert.True(schema.Properties["legacy_label"].Deprecated);
        Assert.Contains("Use Label.", schema.Properties["legacy_label"].Description, StringComparison.Ordinal);
    }

    static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        return builder;
    }

    static async Task<OpenApiDocument> ReadAsync(WebApplication app, string format)
    {
        using var client = app.GetTestClient();
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        var parsed = OpenApiDocument.Parse(await client.GetStringAsync("/openapi/v1." + format),
            format == "yml" ? "yaml" : "json", settings);
        Assert.Empty(parsed.Diagnostic!.Errors);
        return parsed.Document!;
    }
}

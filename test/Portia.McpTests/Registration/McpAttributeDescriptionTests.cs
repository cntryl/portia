using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Cntryl.Portia.Tests;

public sealed class McpAttributeDescriptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldDescribeBothSchemasGivenStandardModelAttributes(bool overrides)
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddPortia().ConfigureJson(options => options.TypeInfoResolver =
                McpAttributeJsonContext.Default.WithAddedModifier(info =>
                {
                    if (info.Type == typeof(AnnotatedTool))
                        info.Properties.Single(property => property.Name == "user_name").Name = "resolver_name";
                }))
            .AddRequestHandler<AnnotatedToolHandler>().AddMcpTool<AnnotatedTool>(options =>
            {
                if (overrides)
                    _ = options.DescribedAs("Application tool.")
                        .DescribeProperty(nameof(AnnotatedTool.UserName), "Application name.", "resolver_name");
            });
        using var provider = services.BuildServiceProvider();

        // Act
        var tool = Assert.Single(provider.GetServices<McpServerTool>()).ProtocolTool;

        // Assert
        Assert.Equal(overrides ? "Application tool." : "Attributed tool.", tool.Description);
        var input = tool.InputSchema.GetProperty("properties");
        Assert.Equal(overrides ? "Application name." : "Attributed user name.",
            input.GetProperty("resolver_name").GetProperty("description").GetString());
        Assert.Equal("Optional details.", input.GetProperty("details").GetProperty("description").GetString());
        var output = tool.OutputSchema!.Value.GetProperty("properties").GetProperty("result");
        Assert.Equal("Attributed payload.", output.GetProperty("description").GetString());
        Assert.Equal("Attributed value.", output.GetProperty("properties").GetProperty("wire-value")
            .GetProperty("description").GetString());
    }

    [Fact]
    public void ShouldUseFriendlyToolTitleGivenDisplayNameAttribute()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<AnnotatedToolHandler>().AddMcpTool<AnnotatedTool>();
        using var provider = services.BuildServiceProvider();

        // Act
        var tool = Assert.Single(provider.GetServices<McpServerTool>()).ProtocolTool;

        // Assert
        Assert.Equal("Friendly tool title", tool.Title);
        Assert.Equal("Friendly tool title", tool.InputSchema.GetProperty("title").GetString());
    }

    [Fact]
    public void ShouldKeepTypeTitleGivenOpaqueConverterSchema()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<OpaqueToolHandler>().AddMcpTool<OpaqueTool>();
        using var provider = services.BuildServiceProvider();

        // Act
        var tool = Assert.Single(provider.GetServices<McpServerTool>()).ProtocolTool;

        // Assert
        Assert.Equal("Opaque payload",
            tool.OutputSchema!.Value.GetProperty("properties").GetProperty("result").GetProperty("title").GetString());
    }

    [Fact]
    public void ShouldExposePopulatedMembersGivenReadOnlyCollectionInput()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<PopulatedToolHandler>().AddMcpTool<PopulatedTool>();
        using var provider = services.BuildServiceProvider();
        var json = provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);

        // Act
        var tool = Assert.Single(provider.GetServices<McpServerTool>()).ProtocolTool;
        var bound = JsonSerializer.Deserialize("{\"values\":[1,2]}", json.GetTypeInfo<PopulatedPayload>());

        // Assert
        Assert.Equal([1, 2], bound!.Values);
        Assert.True(tool.InputSchema.GetProperty("properties").GetProperty("payload").GetProperty("properties")
            .TryGetProperty("values", out _));
    }

    [Fact]
    public void ShouldAcceptActualOutputGivenConditionalPropertyOmission()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<ConditionalToolHandler>().AddMcpTool<ConditionalTool>();
        using var provider = services.BuildServiceProvider();
        var json = provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);

        // Act
        var tool = Assert.Single(provider.GetServices<McpServerTool>()).ProtocolTool;
        using var actual = JsonDocument.Parse("{\"result\":" +
                                              JsonSerializer.Serialize(new ConditionalReply(0),
                                                  json.GetTypeInfo<ConditionalReply>()) + "}");

        // Assert
        Assert.False(actual.RootElement.GetProperty("result").TryGetProperty("count", out _));
        var schema = JsonSchema.Build(tool.OutputSchema!.Value,
            new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() });
        Assert.True(schema.Evaluate(actual.RootElement).IsValid);
    }

    [Fact]
    public void ShouldPreserveReferenceDataGivenSharedOutputExample()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddPortia().AddExample("reference", new ReferencePayload("#/data"), use: PortiaContractUse.Output)
            .AddRequestHandler<ReferenceToolHandler>().AddMcpTool<ReferenceTool>();
        using var provider = services.BuildServiceProvider();

        // Act
        var tool = Assert.Single(provider.GetServices<McpServerTool>()).ProtocolTool;

        // Assert
        var example =
            tool.OutputSchema!.Value.GetProperty("properties").GetProperty("result").GetProperty("examples")[0];
        Assert.Equal("#/data", example.GetProperty("$ref").GetString());
    }

    [Fact]
    public void ShouldSerializeDirectionalExamplesGivenWriteOnlyMember()
    {
        // Arrange
        var services = new ServiceCollection();
        var value = new DirectionalTool("secret", "visible");
        _ = services.AddPortia().AddExample("direction", value).AddRequestHandler<DirectionalToolHandler>()
            .AddMcpTool<DirectionalTool>();
        using var provider = services.BuildServiceProvider();

        // Act
        var tool = Assert.Single(provider.GetServices<McpServerTool>()).ProtocolTool;

        // Assert
        var input = tool.InputSchema.GetProperty("examples")[0];
        var output = tool.OutputSchema!.Value.GetProperty("properties").GetProperty("result")
            .GetProperty("examples")[0];
        Assert.Equal("secret", input.GetProperty("secret").GetString());
        Assert.False(output.TryGetProperty("secret", out _));
        Assert.Equal("visible", output.GetProperty("public_value").GetString());
    }

    [Discriminator("contract.reference")]
    public sealed record ReferenceTool : IRequest<ReferencePayload>, ICallable;

    public sealed record ReferencePayload([property: JsonPropertyName("$ref")] string Reference);

    public sealed class ReferenceToolHandler : IRequestHandler<ReferenceTool, ReferencePayload>
    {
        public ValueTask<Result<ReferencePayload>> HandleAsync(IRequestContext<ReferenceTool> context,
            CancellationToken ct) =>
            ValueTask.FromResult(Result<ReferencePayload>.Success(new("#/data")));
    }

    [Discriminator("contract.direction")]
    public sealed record DirectionalTool(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
        string Secret,
        string PublicValue) : IRequest<DirectionalTool>, ICallable;

    public sealed class DirectionalToolHandler : IRequestHandler<DirectionalTool, DirectionalTool>
    {
        public ValueTask<Result<DirectionalTool>> HandleAsync(IRequestContext<DirectionalTool> context,
            CancellationToken ct) =>
            ValueTask.FromResult(Result<DirectionalTool>.Success(context.Request));
    }

    [Discriminator("contract.populated")]
    public sealed record PopulatedTool(PopulatedPayload Payload) : IRequest<PopulatedPayload>, ICallable;

    public sealed class PopulatedPayload
    {
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public ICollection<int> Values { get; } = [];
    }

    public sealed class PopulatedToolHandler : IRequestHandler<PopulatedTool, PopulatedPayload>
    {
        public ValueTask<Result<PopulatedPayload>> HandleAsync(IRequestContext<PopulatedTool> context,
            CancellationToken ct) =>
            ValueTask.FromResult(Result<PopulatedPayload>.Success(context.Request.Payload));
    }

    [Discriminator("contract.conditional")]
    public sealed record ConditionalTool : IRequest<ConditionalReply>, ICallable;

    public sealed record ConditionalReply(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        int Count);

    public sealed class ConditionalToolHandler : IRequestHandler<ConditionalTool, ConditionalReply>
    {
        public ValueTask<Result<ConditionalReply>> HandleAsync(IRequestContext<ConditionalTool> context,
            CancellationToken ct) =>
            ValueTask.FromResult(Result<ConditionalReply>.Success(new(0)));
    }

    [Discriminator("contract.opaque")]
    public sealed record OpaqueTool : IRequest<OpaquePayload>, ICallable;

    [DisplayName("Opaque payload"), JsonConverter(typeof(OpaquePayloadConverter))]
    public sealed record OpaquePayload(string Value);

    public sealed class OpaquePayloadConverter : JsonConverter<OpaquePayload>
    {
        public override OpaquePayload
            Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, OpaquePayload value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    public sealed class OpaqueToolHandler : IRequestHandler<OpaqueTool, OpaquePayload>
    {
        public ValueTask<Result<OpaquePayload>>
            HandleAsync(IRequestContext<OpaqueTool> context, CancellationToken ct) =>
            ValueTask.FromResult(Result<OpaquePayload>.Success(new("value")));
    }

    [DisplayName("Friendly tool title")]
    [Description("Attributed tool.")]
    [Discriminator("attributes.describe")]
    public sealed record AnnotatedTool(
        [property: Description("Attributed user name.")]
        string UserName,
        [property: System.ComponentModel.DataAnnotations.Display(Description = "Optional details.")]
        AnnotatedPayload? Details = null) : IRequest<AnnotatedPayload>, ICallable;

    [Description("Attributed payload.")]
    public sealed record AnnotatedPayload(
        [property: Description("Attributed value."), JsonPropertyName("wire-value")]
        string Value);

    public sealed class AnnotatedToolHandler : IRequestHandler<AnnotatedTool, AnnotatedPayload>
    {
        public ValueTask<Result<AnnotatedPayload>> HandleAsync(IRequestContext<AnnotatedTool> context,
            CancellationToken ct) =>
            ValueTask.FromResult(Result<AnnotatedPayload>.Success(new AnnotatedPayload(context.Request.UserName)));
    }
}

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(McpAttributeDescriptionTests.ReferenceTool))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.ReferencePayload))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.DirectionalTool))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.PopulatedTool))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.PopulatedPayload))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.ConditionalTool))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.ConditionalReply))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.OpaqueTool))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.OpaquePayload))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.AnnotatedTool))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.AnnotatedPayload))]
sealed partial class McpAttributeJsonContext : JsonSerializerContext;

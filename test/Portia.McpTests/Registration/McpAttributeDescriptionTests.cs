using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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

    [Description("Attributed tool.")]
    [Discriminator("attributes.describe")]
    public sealed record AnnotatedTool(
        [property: Description("Attributed user name.")] string UserName,
        [property: System.ComponentModel.DataAnnotations.Display(Description = "Optional details.")]
        AnnotatedPayload? Details = null) : IRequest<AnnotatedPayload>, ICallable;

    [Description("Attributed payload.")]
    public sealed record AnnotatedPayload([property: Description("Attributed value."), JsonPropertyName("wire-value")] string Value);

    public sealed class AnnotatedToolHandler : IRequestHandler<AnnotatedTool, AnnotatedPayload>
    {
        public ValueTask<Result<AnnotatedPayload>> HandleAsync(IRequestContext<AnnotatedTool> context,
            CancellationToken ct) => ValueTask.FromResult(Result<AnnotatedPayload>.Success(new AnnotatedPayload(context.Request.UserName)));
    }
}

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(McpAttributeDescriptionTests.AnnotatedTool))]
[JsonSerializable(typeof(McpAttributeDescriptionTests.AnnotatedPayload))]
sealed partial class McpAttributeJsonContext : JsonSerializerContext;

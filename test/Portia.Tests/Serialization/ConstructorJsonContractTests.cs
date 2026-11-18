using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>Strict input respects immutable constructors without changing optional nullable parameters.</summary>
public sealed class ConstructorJsonContractTests
{
    /// <summary>Constructor presence and nullability work without assigning a getter-only member.</summary>
    [Theory]
    [InlineData("{\"text\":\"valid\"}", true)]
    [InlineData("{\"text\":\"valid\",\"note\":null}", true)]
    [InlineData("{}", false)]
    [InlineData("{\"text\":null}", false)]
    public void ShouldBindStrictInputGivenGetterOnlyConstructor(string json, bool valid)
    {
        // Arrange
        var source = ConstructorContractJsonContext.Default.Options;
        var strict = PortiaJsonRequestOptions.Strict(source);
        ImmutableConstructorContract? bound = null;

        // Act
        var exception = Record.Exception(() =>
            bound = JsonSerializer.Deserialize(json, strict.GetTypeInfo<ImmutableConstructorContract>()));

        // Assert
        if (valid)
        {
            Assert.Null(exception);
            Assert.Equal("valid", bound!.Text);
            Assert.Null(bound.Note);
            Assert.Equal(7, bound.Count);
        }
        else
            Assert.IsType<JsonException>(exception);

        Assert.False(source.RespectRequiredConstructorParameters);
    }

    /// <summary>Explicit constructor policy and explicit JSON presence remain authoritative for nullable members.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRequireNullableMemberGivenExplicitPresencePolicy(bool constructorPolicy)
    {
        // Arrange
        var source = new JsonSerializerOptions(ConstructorContractJsonContext.Default.Options)
        {
            RespectRequiredConstructorParameters = constructorPolicy
        };
        var strict = PortiaJsonRequestOptions.Strict(source);
        var type = constructorPolicy ? typeof(ImmutableConstructorContract) : typeof(JsonRequiredConstructorContract);

        // Act
        var exception =
            Record.Exception(() => JsonSerializer.Deserialize("{\"text\":\"valid\"}", strict.GetTypeInfo(type)));

        // Assert
        var failure = Assert.IsType<JsonException>(exception);
        Assert.Contains("note", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Schema presence reflects the selectively required input members.</summary>
    [Fact]
    public void ShouldAdvertiseOptionalNullableMemberGivenImmutableConstructor()
    {
        // Arrange
        var source = ConstructorContractJsonContext.Default.Options;
        var options = PortiaJsonSchemaProjection.For(source, source, input: true);

        // Act
        var schema = options.GetTypeInfo<ImmutableConstructorContract>().GetJsonSchemaAsNode(
            new JsonSchemaExporterOptions
            {
                TransformSchemaNode = (context, node) =>
                    PortiaJsonDescriptions.Apply(context, node, source, input: true)
            });

        // Assert
        var required = schema["required"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(["text"], required);
        Assert.True(schema["properties"]!.AsObject().ContainsKey("note"));
    }
}

sealed class ImmutableConstructorContract(string text, string? note, int count = 7)
{
    public string Text { get; } = text;
    public string? Note { get; } = note;
    public int Count { get; } = count;
}

sealed class JsonRequiredConstructorContract(string text, string? note)
{
    public string Text { get; } = text;
    [JsonRequired] public string? Note { get; } = note;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ImmutableConstructorContract))]
[JsonSerializable(typeof(JsonRequiredConstructorContract))]
sealed partial class ConstructorContractJsonContext : JsonSerializerContext;

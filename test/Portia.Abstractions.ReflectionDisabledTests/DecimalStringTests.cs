using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Cntryl.Portia;

public sealed class DecimalStringTests
{
    [Theory]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001")]
    public void ShouldRoundTripGivenExactValueWhenUsingGeneratedJson(string text)
    {
        // Arrange
        var type = DecimalStringTestJsonContext.Default.DecimalString;
        var json = "\"" + text + "\"";

        // Act
        var value = JsonSerializer.Deserialize(json, type);
        var roundTrip = JsonSerializer.Serialize(value, type);

        // Assert
        Assert.Equal(json, roundTrip);
        Assert.True(DecimalString.TryParse(text, out var parsed));
        Assert.Equal(parsed, value);
    }

    [Theory]
    [InlineData("79228162514264337593543950336")]
    [InlineData("79228162514264337593543950335.1")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("1e0")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData(" 1")]
    public void ShouldRejectGivenUnrepresentableOrInvalidDecimalSpelling(string text)
    {
        // Arrange
        var type = DecimalStringTestJsonContext.Default.DecimalString;
        var json = "\"" + text + "\"";

        // Act
        var parsed = DecimalString.TryParse(text, out _);
        var error = Record.Exception(() => JsonSerializer.Deserialize(json, type));

        // Assert
        Assert.False(parsed);
        Assert.IsType<JsonException>(error);
    }

    [Fact]
    public void ShouldRejectNumberTokenGivenDecimalStringContract()
    {
        // Arrange
        var type = DecimalStringTestJsonContext.Default.DecimalString;

        // Act
        var error = Record.Exception(() => JsonSerializer.Deserialize("1.25", type));

        // Assert
        Assert.IsType<JsonException>(error);
    }
}

[JsonSerializable(typeof(DecimalString))]
sealed partial class DecimalStringTestJsonContext : JsonSerializerContext;

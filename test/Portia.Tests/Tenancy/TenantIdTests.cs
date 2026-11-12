using System.Globalization;
using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="TenantId" /> binds and serializes the way <see cref="Uuid" /> does, so a request
///     can declare a typed tenant and a malformed one is refused where it arrives rather than inside a
///     handler as a stream-address exception.
/// </summary>
public sealed class TenantIdTests
{
    /// <summary>A well-formed segment parses to the tenant it names.</summary>
    [Fact]
    public void ShouldParseWellFormedSegment()
    {
        // Arrange
        const string text = "acme";

        // Act
        var tried = TenantId.TryParse(text, CultureInfo.InvariantCulture, out var parsed);
        var fromString = TenantId.Parse(text, CultureInfo.InvariantCulture);
        var fromSpan = TenantId.Parse(text.AsSpan(), CultureInfo.InvariantCulture);

        // Assert
        Assert.True(tried);
        Assert.Equal(new TenantId("acme"), parsed);
        Assert.Equal(new TenantId("acme"), fromString);
        Assert.Equal(new TenantId("acme"), fromSpan);
    }

    /// <summary>Text that cannot be a realm segment fails to parse instead of throwing.</summary>
    /// <param name="text">The rejected text.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a*b")]
    [InlineData("{tenant}")]
    public void ShouldRejectMalformedSegment(string text)
    {
        // Arrange
        // The rejected text arrives as the theory argument.

        // Act
        var triedString = TenantId.TryParse(text, CultureInfo.InvariantCulture, out var parsed);
        var triedSpan = TenantId.TryParse(text.AsSpan(), CultureInfo.InvariantCulture, out _);
        var exception = Record.Exception(() => TenantId.Parse(text, CultureInfo.InvariantCulture));

        // Assert
        Assert.False(triedString);
        Assert.Equal(default, parsed);
        Assert.False(triedSpan);
        _ = Assert.IsType<FormatException>(exception);
    }

    /// <summary>A null string fails to parse without throwing.</summary>
    [Fact]
    public void ShouldRejectNullText()
    {
        // Arrange
        string? text = null;

        // Act
        var tried = TenantId.TryParse(text, CultureInfo.InvariantCulture, out _);

        // Assert
        Assert.False(tried);
    }

    /// <summary>The tenant formats as its bare value, including through the span API.</summary>
    [Fact]
    public void ShouldFormatAsBareValue()
    {
        // Arrange
        var tenant = new TenantId("acme");

        // Act
        Span<char> buffer = stackalloc char[8];

        // Assert
        Assert.True(tenant.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture));
        Assert.Equal("acme", buffer[..written].ToString());
        Assert.Equal("acme", tenant.ToString(null, CultureInfo.InvariantCulture));
        Assert.False(tenant.TryFormat(buffer[..2], out written, default, CultureInfo.InvariantCulture));
        Assert.Equal(0, written);
    }

    /// <summary>The converter on the type itself serializes a tenant as a plain JSON string.</summary>
    [Fact]
    public void ShouldSerializeAsPlainString()
    {
        // Arrange
        var tenant = new TenantId("acme");

        // Act
        var json = JsonSerializer.Serialize(tenant);
        var deserialized = JsonSerializer.Deserialize<TenantId>("\"acme\"");

        // Assert
        Assert.Equal("\"acme\"", json);
        Assert.Equal(new TenantId("acme"), deserialized);
    }

    /// <summary>A JSON string that is not a realm segment is a JSON error, not an argument error.</summary>
    [Fact]
    public void ShouldRejectMalformedJsonString()
    {
        // Arrange
        const string json = "\"a/b\"";

        // Act
        var exception = Record.Exception(() => JsonSerializer.Deserialize<TenantId>(json));

        // Assert
        _ = Assert.IsType<JsonException>(exception);
    }

    /// <summary>A tenant can key a JSON dictionary.</summary>
    [Fact]
    public void ShouldRoundTripAsDictionaryKey()
    {
        // Arrange
        var dictionary = new Dictionary<TenantId, int> { [new TenantId("acme")] = 1 };

        // Act
        var json = JsonSerializer.Serialize(dictionary);
        var roundTripped = JsonSerializer.Deserialize<Dictionary<TenantId, int>>(json);

        // Assert
        Assert.Equal("""{"acme":1}""", json);
        Assert.Equal(1, roundTripped![new TenantId("acme")]);
    }
}

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
        Assert.True(TenantId.TryParse("acme", CultureInfo.InvariantCulture, out var parsed));
        Assert.Equal(new TenantId("acme"), parsed);
        Assert.Equal(new TenantId("acme"), TenantId.Parse("acme", CultureInfo.InvariantCulture));
        Assert.Equal(new TenantId("acme"), TenantId.Parse("acme".AsSpan(), CultureInfo.InvariantCulture));
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
        Assert.False(TenantId.TryParse(text, CultureInfo.InvariantCulture, out var parsed));
        Assert.Equal(default, parsed);
        Assert.False(TenantId.TryParse(text.AsSpan(), CultureInfo.InvariantCulture, out _));
        _ = Assert.Throws<FormatException>(() => TenantId.Parse(text, CultureInfo.InvariantCulture));
    }

    /// <summary>A null string fails to parse without throwing.</summary>
    [Fact]
    public void ShouldRejectNullText() =>
        Assert.False(TenantId.TryParse(null, CultureInfo.InvariantCulture, out _));

    /// <summary>The tenant formats as its bare value, including through the span API.</summary>
    [Fact]
    public void ShouldFormatAsBareValue()
    {
        var tenant = new TenantId("acme");
        Span<char> buffer = stackalloc char[8];

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
        Assert.Equal("\"acme\"", JsonSerializer.Serialize(new TenantId("acme")));
        Assert.Equal(new TenantId("acme"), JsonSerializer.Deserialize<TenantId>("\"acme\""));
    }

    /// <summary>A JSON string that is not a realm segment is a JSON error, not an argument error.</summary>
    [Fact]
    public void ShouldRejectMalformedJsonString() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TenantId>("\"a/b\""));

    /// <summary>A tenant can key a JSON dictionary.</summary>
    [Fact]
    public void ShouldRoundTripAsDictionaryKey()
    {
        var json = JsonSerializer.Serialize(new Dictionary<TenantId, int> { [new TenantId("acme")] = 1 });

        Assert.Equal("""{"acme":1}""", json);
        Assert.Equal(1, JsonSerializer.Deserialize<Dictionary<TenantId, int>>(json)![new TenantId("acme")]);
    }
}

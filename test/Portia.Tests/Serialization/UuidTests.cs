using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>
///     Verifies UUID generation and representation.
/// </summary>
public sealed class UuidTests
{
    const string CanonicalText = "21f7f8de-8051-5b89-8680-0195ef798b6a";

    /// <summary>
    ///     Verifies UUID version 5 against the RFC name-based example.
    /// </summary>
    [Fact]
    public void ShouldCreateKnownVersion5UuidForDnsName()
    {
        // Arrange
        const string name = "www.widgets.com";

        // Act
        var uuid = Uuid.CreateVersion5(Uuid.DnsNamespace, name);

        // Assert
        Assert.Equal("21f7f8de-8051-5b89-8680-0195ef798b6a", uuid.ToString());
        Assert.Equal(5, uuid.Version);
    }

    /// <summary>
    ///     Verifies that identical namespace and name inputs produce identical UUIDs.
    /// </summary>
    [Fact]
    public void ShouldCreateSameVersion5UuidForSameNamespaceAndName()
    {
        // Arrange
        var first = Uuid.CreateVersion5(Uuid.UrlNamespace, "https://cntryl.com/aggregates/42");

        // Act
        var second = Uuid.CreateVersion5(Uuid.UrlNamespace, "https://cntryl.com/aggregates/42");

        // Assert
        Assert.Equal(first, second);
    }

    /// <summary>
    ///     Verifies that the text overload uses the same UTF-8 representation as the byte overload.
    /// </summary>
    [Fact]
    public void ShouldCreateSameVersion5UuidFromTextAndUtf8Bytes()
    {
        // Arrange
        const string name = "café/東京/🦋";

        var fromText = Uuid.CreateVersion5(Uuid.UrlNamespace, name);

        // Act
        var fromBytes = Uuid.CreateVersion5(Uuid.UrlNamespace, Encoding.UTF8.GetBytes(name));

        // Assert
        Assert.Equal(fromText, fromBytes);
    }

    /// <summary>
    ///     Verifies safe version 5 generation for names larger than the stack-buffer threshold.
    /// </summary>
    [Fact]
    public void ShouldCreateVersion5UuidForLargeName()
    {
        // Arrange
        var name = new string('a', 4096);

        var first = Uuid.CreateVersion5(Uuid.UrlNamespace, name);

        // Act
        var second = Uuid.CreateVersion5(Uuid.UrlNamespace, Encoding.UTF8.GetBytes(name));

        // Assert
        Assert.Equal(first, second);
        Assert.Equal(5, first.Version);
    }

    /// <summary>
    ///     Verifies that namespaces participate in version 5 identity.
    /// </summary>
    [Fact]
    public void ShouldCreateDifferentVersion5UuidForDifferentNamespace()
    {
        // Arrange
        var dns = Uuid.CreateVersion5(Uuid.DnsNamespace, "cntryl.com");

        // Act
        var url = Uuid.CreateVersion5(Uuid.UrlNamespace, "cntryl.com");

        // Assert
        Assert.NotEqual(dns, url);
    }

    /// <summary>
    ///     Verifies that generated UUID version 4 values report the correct version.
    /// </summary>
    [Fact]
    public void ShouldCreateVersion4Uuid()
    {
        // Arrange
        // No setup: version 4 generation takes no inputs.

        // Act
        var uuid = Uuid.CreateVersion4();

        // Assert
        Assert.NotEqual(Uuid.Empty, uuid);
        Assert.Equal(4, uuid.Version);
    }

    /// <summary>
    ///     Verifies canonical text parsing and formatting.
    /// </summary>
    [Fact]
    public void ShouldRoundTripCanonicalUuidText()
    {
        // Arrange
        const string text = CanonicalText;

        // Act
        var uuid = Uuid.Parse(text, CultureInfo.InvariantCulture);
        var formatted = uuid.ToString();
        var tried = Uuid.TryParse(text, out var parsed);

        // Assert
        Assert.Equal(CanonicalText, formatted);
        Assert.True(tried);
        Assert.Equal(uuid, parsed);
    }

    /// <summary>Verifies string and span parsing through their generic contracts.</summary>
    [Fact]
    public void ShouldParseThroughGenericContractsWithGuidParity()
    {
        // Arrange
        var expected = Guid.Parse(CanonicalText, CultureInfo.InvariantCulture);

        var fromString = Parse<Uuid>(CanonicalText);

        // Act
        var fromSpan = ParseSpan<Uuid>(CanonicalText);

        // Assert
        Assert.Equal(expected, fromString.ToGuid());
        Assert.Equal(expected, fromSpan.ToGuid());
        Assert.True(TryParse<Uuid>(CanonicalText, out var triedString));
        Assert.True(TryParseSpan<Uuid>(CanonicalText, out var triedSpan));
        Assert.Equal(fromString, triedString);
        Assert.Equal(fromSpan, triedSpan);
    }

    /// <summary>Verifies generic parsing preserves <see cref="Guid" />'s invalid-input behavior.</summary>
    [Fact]
    public void ShouldRejectInvalidInputThroughGenericParsingContracts()
    {
        // Arrange
        const string invalid = "not-a-uuid";

        // Act
        var parseError = Record.Exception(() => Parse<Uuid>(invalid));
        var parseSpanError = Record.Exception(() => ParseSpan<Uuid>(invalid));
        var triedString = TryParse<Uuid>(invalid, out var fromString);
        var triedSpan = TryParseSpan<Uuid>(invalid, out var fromSpan);

        // Assert
        _ = Assert.IsType<FormatException>(parseError);
        _ = Assert.IsType<FormatException>(parseSpanError);
        Assert.False(triedString);
        Assert.False(triedSpan);
        Assert.Equal(Uuid.Empty, fromString);
        Assert.Equal(Uuid.Empty, fromSpan);
    }

    /// <summary>Verifies string and span formatting through their generic contracts.</summary>
    /// <param name="format">The standard <see cref="Guid" /> format to verify.</param>
    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    [InlineData("X")]
    public void ShouldFormatThroughGenericContractsWithGuidParity(string format)
    {
        // Arrange
        var guid = Guid.Parse(CanonicalText, CultureInfo.InvariantCulture);
        var uuid = Uuid.FromGuid(guid);

        // Act
        var expected = guid.ToString(format, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal(expected, Format(uuid, format));
        Assert.Equal(expected, FormatSpan(uuid, format));
    }

    /// <summary>Verifies generic comparison follows the wrapped <see cref="Guid" /> ordering.</summary>
    [Fact]
    public void ShouldCompareThroughGenericContractWithGuidParity()
    {
        // Arrange
        var firstGuid = Guid.Parse("00000000-0000-0000-0000-000000000001", CultureInfo.InvariantCulture);
        var secondGuid = Guid.Parse("00000000-0000-0000-0000-000000000002", CultureInfo.InvariantCulture);
        var first = Uuid.FromGuid(firstGuid);

        // Act
        var second = Uuid.FromGuid(secondGuid);

        // Assert
        Assert.Equal(Math.Sign(firstGuid.CompareTo(secondGuid)), Math.Sign(Compare(first, second)));
        Assert.Equal(Math.Sign(secondGuid.CompareTo(firstGuid)), Math.Sign(Compare(second, first)));
        Assert.Equal(0, Compare(first, first));
    }

    /// <summary>
    ///     Verifies failed parsing returns the empty UUID sentinel.
    /// </summary>
    [Fact]
    public void ShouldReturnEmptyUuidWhenTryParseFails()
    {
        // Arrange
        const string invalid = "not-a-uuid";

        // Act
        var parsed = Uuid.TryParse(invalid, out var uuid);

        // Assert
        Assert.False(parsed);
        Assert.Equal(Uuid.Empty, uuid);
    }

    /// <summary>
    ///     Verifies that a UUID serializes as a plain canonical string, not its default struct shape
    ///     (which would otherwise just be the public <see cref="Uuid.Version" /> property) —
    ///     <see cref="UuidJsonConverter" /> is picked up automatically via <c>[JsonConverter]</c> on
    ///     <see cref="Uuid" /> itself, with no per-<see cref="JsonSerializerOptions" />
    ///     setup required.
    /// </summary>
    [Fact]
    public void ShouldSerializeAsPlainCanonicalString()
    {
        // Arrange
        var uuid = Uuid.Parse("21f7f8de-8051-5b89-8680-0195ef798b6a", CultureInfo.InvariantCulture);

        // Act
        var json = JsonSerializer.Serialize(uuid);

        // Assert
        Assert.Equal("\"21f7f8de-8051-5b89-8680-0195ef798b6a\"", json);
    }

    /// <summary>
    ///     Verifies that a UUID deserializes from its canonical string form back to an equal value.
    /// </summary>
    [Fact]
    public void ShouldDeserializeFromCanonicalString()
    {
        // Arrange
        const string json = "\"21f7f8de-8051-5b89-8680-0195ef798b6a\"";

        // Act
        var deserialized = JsonSerializer.Deserialize<Uuid>(json);

        // Assert
        Assert.Equal(Uuid.Parse("21f7f8de-8051-5b89-8680-0195ef798b6a", CultureInfo.InvariantCulture), deserialized);
    }

    /// <summary>
    ///     Verifies that deserializing an invalid UUID string throws rather than silently producing
    ///     <see cref="Uuid.Empty" /> — unlike <c>TryParse</c>, JSON deserialization has
    ///     no natural "did it work" boolean to report failure through, so this is the correct failure
    ///     channel.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenDeserializingInvalidUuidString()
    {
        // Arrange
        const string json = "\"not-a-uuid\"";

        // Act
        var exception = Record.Exception(() => JsonSerializer.Deserialize<Uuid>(json));

        // Assert
        _ = Assert.IsType<JsonException>(exception);
    }

    /// <summary>A UUID-keyed dictionary round-trips with each key in its canonical string form.</summary>
    [Fact]
    public void ShouldRoundTripAsDictionaryKey()
    {
        // Arrange
        var uuid = Uuid.Parse("21f7f8de-8051-5b89-8680-0195ef798b6a", CultureInfo.InvariantCulture);

        var json = JsonSerializer.Serialize(new Dictionary<Uuid, int> { [uuid] = 7 });

        // Act
        var deserialized = JsonSerializer.Deserialize<Dictionary<Uuid, int>>(json);

        // Assert
        Assert.Equal("{\"21f7f8de-8051-5b89-8680-0195ef798b6a\":7}", json);
        Assert.Equal(7, Assert.Contains(uuid, deserialized!));
    }

    /// <summary>A key that is not a UUID fails as JSON rather than becoming <see cref="Uuid.Empty" />.</summary>
    [Fact]
    public void ShouldThrowWhenDeserializingInvalidUuidKey()
    {
        // Arrange
        const string json = "{\"x\":1}";

        // Act
        var exception = Record.Exception(() => JsonSerializer.Deserialize<Dictionary<Uuid, int>>(json));

        // Assert
        _ = Assert.IsType<JsonException>(exception);
    }

    static T Parse<T>(string value) where T : IParsable<T> =>
        T.Parse(value, CultureInfo.InvariantCulture);

    static T ParseSpan<T>(string value) where T : ISpanParsable<T> =>
        T.Parse(value.AsSpan(), CultureInfo.InvariantCulture);

    static bool TryParse<T>(string value, [MaybeNullWhen(false)] out T parsed) where T : IParsable<T> =>
        T.TryParse(value, CultureInfo.InvariantCulture, out parsed);

    static bool TryParseSpan<T>(string value, [MaybeNullWhen(false)] out T parsed) where T : ISpanParsable<T> =>
        T.TryParse(value.AsSpan(), CultureInfo.InvariantCulture, out parsed);

    static string Format<T>(T value, string format) where T : IFormattable =>
        value.ToString(format, CultureInfo.InvariantCulture);

    static string FormatSpan<T>(T value, string format) where T : ISpanFormattable
    {
        Span<char> destination = stackalloc char[68];
        Assert.True(value.TryFormat(destination, out var charsWritten, format, CultureInfo.InvariantCulture));
        return new string(destination[..charsWritten]);
    }

    static int Compare<T>(T left, T right) where T : IComparable<T> => left.CompareTo(right);
}

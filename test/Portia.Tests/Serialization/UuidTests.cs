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
        var uuid = Uuid.CreateVersion5(Uuid.DnsNamespace, "www.widgets.com");

        Assert.Equal("21f7f8de-8051-5b89-8680-0195ef798b6a", uuid.ToString());
        Assert.Equal(5, uuid.Version);
    }

    /// <summary>
    ///     Verifies that identical namespace and name inputs produce identical UUIDs.
    /// </summary>
    [Fact]
    public void ShouldCreateSameVersion5UuidForSameNamespaceAndName()
    {
        var first = Uuid.CreateVersion5(Uuid.UrlNamespace, "https://cntryl.com/aggregates/42");
        var second = Uuid.CreateVersion5(Uuid.UrlNamespace, "https://cntryl.com/aggregates/42");

        Assert.Equal(first, second);
    }

    /// <summary>
    ///     Verifies that the text overload uses the same UTF-8 representation as the byte overload.
    /// </summary>
    [Fact]
    public void ShouldCreateSameVersion5UuidFromTextAndUtf8Bytes()
    {
        const string name = "café/東京/🦋";

        var fromText = Uuid.CreateVersion5(Uuid.UrlNamespace, name);
        var fromBytes = Uuid.CreateVersion5(Uuid.UrlNamespace, Encoding.UTF8.GetBytes(name));

        Assert.Equal(fromText, fromBytes);
    }

    /// <summary>
    ///     Verifies safe version 5 generation for names larger than the stack-buffer threshold.
    /// </summary>
    [Fact]
    public void ShouldCreateVersion5UuidForLargeName()
    {
        var name = new string('a', 4096);

        var first = Uuid.CreateVersion5(Uuid.UrlNamespace, name);
        var second = Uuid.CreateVersion5(Uuid.UrlNamespace, Encoding.UTF8.GetBytes(name));

        Assert.Equal(first, second);
        Assert.Equal(5, first.Version);
    }

    /// <summary>
    ///     Verifies that namespaces participate in version 5 identity.
    /// </summary>
    [Fact]
    public void ShouldCreateDifferentVersion5UuidForDifferentNamespace()
    {
        var dns = Uuid.CreateVersion5(Uuid.DnsNamespace, "cntryl.com");
        var url = Uuid.CreateVersion5(Uuid.UrlNamespace, "cntryl.com");

        Assert.NotEqual(dns, url);
    }

    /// <summary>
    ///     Verifies that generated UUID version 4 values report the correct version.
    /// </summary>
    [Fact]
    public void ShouldCreateVersion4Uuid()
    {
        var uuid = Uuid.CreateVersion4();

        Assert.NotEqual(Uuid.Empty, uuid);
        Assert.Equal(4, uuid.Version);
    }

    /// <summary>
    ///     Verifies canonical text parsing and formatting.
    /// </summary>
    [Fact]
    public void ShouldRoundTripCanonicalUuidText()
    {
        var uuid = Uuid.Parse(CanonicalText, CultureInfo.InvariantCulture);

        Assert.Equal(CanonicalText, uuid.ToString());
        Assert.True(Uuid.TryParse(CanonicalText, out var parsed));
        Assert.Equal(uuid, parsed);
    }

    /// <summary>Verifies string and span parsing through their generic contracts.</summary>
    [Fact]
    public void ShouldParseThroughGenericContractsWithGuidParity()
    {
        var expected = Guid.Parse(CanonicalText, CultureInfo.InvariantCulture);

        var fromString = Parse<Uuid>(CanonicalText);
        var fromSpan = ParseSpan<Uuid>(CanonicalText);

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
        _ = Assert.Throws<FormatException>(() => Parse<Uuid>("not-a-uuid"));
        _ = Assert.Throws<FormatException>(() => ParseSpan<Uuid>("not-a-uuid"));

        Assert.False(TryParse<Uuid>("not-a-uuid", out var fromString));
        Assert.False(TryParseSpan<Uuid>("not-a-uuid", out var fromSpan));
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
        var guid = Guid.Parse(CanonicalText, CultureInfo.InvariantCulture);
        var uuid = Uuid.FromGuid(guid);
        var expected = guid.ToString(format, CultureInfo.InvariantCulture);

        Assert.Equal(expected, Format(uuid, format));
        Assert.Equal(expected, FormatSpan(uuid, format));
    }

    /// <summary>Verifies generic comparison follows the wrapped <see cref="Guid" /> ordering.</summary>
    [Fact]
    public void ShouldCompareThroughGenericContractWithGuidParity()
    {
        var firstGuid = Guid.Parse("00000000-0000-0000-0000-000000000001", CultureInfo.InvariantCulture);
        var secondGuid = Guid.Parse("00000000-0000-0000-0000-000000000002", CultureInfo.InvariantCulture);
        var first = Uuid.FromGuid(firstGuid);
        var second = Uuid.FromGuid(secondGuid);

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
        var parsed = Uuid.TryParse("not-a-uuid", out var uuid);

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
        var uuid = Uuid.Parse("21f7f8de-8051-5b89-8680-0195ef798b6a", CultureInfo.InvariantCulture);

        var json = JsonSerializer.Serialize(uuid);

        Assert.Equal("\"21f7f8de-8051-5b89-8680-0195ef798b6a\"", json);
    }

    /// <summary>
    ///     Verifies that a UUID deserializes from its canonical string form back to an equal value.
    /// </summary>
    [Fact]
    public void ShouldDeserializeFromCanonicalString()
    {
        var deserialized = JsonSerializer.Deserialize<Uuid>("\"21f7f8de-8051-5b89-8680-0195ef798b6a\"");

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
        _ = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Uuid>("\"not-a-uuid\""));
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

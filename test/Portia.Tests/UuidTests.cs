using System.Text;

namespace Cntryl.Portia;

/// <summary>
/// Verifies UUID generation and representation.
/// </summary>
public sealed class UuidTests
{
    /// <summary>
    /// Verifies UUID version 5 against the RFC name-based example.
    /// </summary>
    [Fact]
    public void ShouldCreateKnownVersion5UuidForDnsName()
    {
        var uuid = Uuid.CreateVersion5(Uuid.DnsNamespace, "www.widgets.com");

        Assert.Equal("21f7f8de-8051-5b89-8680-0195ef798b6a", uuid.ToString());
        Assert.Equal(5, uuid.Version);
    }

    /// <summary>
    /// Verifies that identical namespace and name inputs produce identical UUIDs.
    /// </summary>
    [Fact]
    public void ShouldCreateSameVersion5UuidForSameNamespaceAndName()
    {
        var first = Uuid.CreateVersion5(Uuid.UrlNamespace, "https://cntryl.com/aggregates/42");
        var second = Uuid.CreateVersion5(Uuid.UrlNamespace, "https://cntryl.com/aggregates/42");

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Verifies that the text overload uses the same UTF-8 representation as the byte overload.
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
    /// Verifies safe version 5 generation for names larger than the stack-buffer threshold.
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
    /// Verifies that namespaces participate in version 5 identity.
    /// </summary>
    [Fact]
    public void ShouldCreateDifferentVersion5UuidForDifferentNamespace()
    {
        var dns = Uuid.CreateVersion5(Uuid.DnsNamespace, "cntryl.com");
        var url = Uuid.CreateVersion5(Uuid.UrlNamespace, "cntryl.com");

        Assert.NotEqual(dns, url);
    }

    /// <summary>
    /// Verifies that generated UUID version 4 values report the correct version.
    /// </summary>
    [Fact]
    public void ShouldCreateVersion4Uuid()
    {
        var uuid = Uuid.CreateVersion4();

        Assert.NotEqual(Uuid.Empty, uuid);
        Assert.Equal(4, uuid.Version);
    }

    /// <summary>
    /// Verifies canonical text parsing and formatting.
    /// </summary>
    [Fact]
    public void ShouldRoundTripCanonicalUuidText()
    {
        const string text = "21f7f8de-8051-5b89-8680-0195ef798b6a";

        var uuid = Uuid.Parse(text);

        Assert.Equal(text, uuid.ToString());
        Assert.True(Uuid.TryParse(text, out var parsed));
        Assert.Equal(uuid, parsed);
    }

    /// <summary>
    /// Verifies failed parsing returns the empty UUID sentinel.
    /// </summary>
    [Fact]
    public void ShouldReturnEmptyUuidWhenTryParseFails()
    {
        var parsed = Uuid.TryParse("not-a-uuid", out var uuid);

        Assert.False(parsed);
        Assert.Equal(Uuid.Empty, uuid);
    }

    /// <summary>
    /// Verifies that a UUID serializes as a plain canonical string, not its default struct shape
    /// (which would otherwise just be the public <see cref="Uuid.Version" /> property) —
    /// <see cref="UuidJsonConverter" /> is picked up automatically via <c>[JsonConverter]</c> on
    /// <see cref="Uuid" /> itself, with no per-<see cref="System.Text.Json.JsonSerializerOptions" />
    /// setup required.
    /// </summary>
    [Fact]
    public void ShouldSerializeAsPlainCanonicalString()
    {
        var uuid = Uuid.Parse("21f7f8de-8051-5b89-8680-0195ef798b6a");

        var json = System.Text.Json.JsonSerializer.Serialize(uuid);

        Assert.Equal("\"21f7f8de-8051-5b89-8680-0195ef798b6a\"", json);
    }

    /// <summary>
    /// Verifies that a UUID deserializes from its canonical string form back to an equal value.
    /// </summary>
    [Fact]
    public void ShouldDeserializeFromCanonicalString()
    {
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<Uuid>("\"21f7f8de-8051-5b89-8680-0195ef798b6a\"");

        Assert.Equal(Uuid.Parse("21f7f8de-8051-5b89-8680-0195ef798b6a"), deserialized);
    }

    /// <summary>
    /// Verifies that deserializing an invalid UUID string throws rather than silently producing
    /// <see cref="Uuid.Empty" /> — unlike <see cref="Uuid.TryParse" />, JSON deserialization has
    /// no natural "did it work" boolean to report failure through, so this is the correct failure
    /// channel.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenDeserializingInvalidUuidString()
    {
        _ = Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<Uuid>("\"not-a-uuid\""));
    }
}

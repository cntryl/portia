using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>
/// Represents a universally unique identifier.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1036:Override methods on comparable types",
    Justification = "IComparable<Uuid> supplies generic ordering without expanding UUID's operator contract.")]
[JsonConverter(typeof(UuidJsonConverter))]
public readonly record struct Uuid : ISpanParsable<Uuid>, ISpanFormattable, IComparable<Uuid>
{
    const int NamespaceByteLength = 16;
    const int HashByteLength = 20;
    const int StackAllocationThreshold = 512;

    readonly Guid _value;

    Uuid(Guid value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the empty UUID.
    /// </summary>
    public static Uuid Empty => default;

    /// <summary>
    /// Gets the RFC namespace UUID for domain names.
    /// </summary>
    public static Uuid DnsNamespace { get; } = Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets the RFC namespace UUID for URLs.
    /// </summary>
    public static Uuid UrlNamespace { get; } = Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets the RFC namespace UUID for ISO object identifiers.
    /// </summary>
    public static Uuid OidNamespace { get; } = Parse("6ba7b812-9dad-11d1-80b4-00c04fd430c8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets the RFC namespace UUID for X.500 distinguished names.
    /// </summary>
    public static Uuid X500Namespace { get; } = Parse("6ba7b814-9dad-11d1-80b4-00c04fd430c8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets the UUID version.
    /// </summary>
    public int Version => _value.Version;

    /// <summary>
    /// Creates a new random UUID version 4.
    /// </summary>
    /// <returns>A new UUID.</returns>
    public static Uuid CreateVersion4() => new(Guid.NewGuid());

    /// <summary>
    /// Creates a deterministic name-based UUID version 5 using a UTF-8 name.
    /// </summary>
    /// <param name="namespaceId">The namespace UUID.</param>
    /// <param name="name">The name within the namespace.</param>
    /// <returns>The deterministic UUID.</returns>
    public static Uuid CreateVersion5(Uuid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var nameByteLength = Encoding.UTF8.GetByteCount(name);
        var inputByteLength = checked(NamespaceByteLength + nameByteLength);

        if (inputByteLength <= StackAllocationThreshold)
        {
            Span<byte> input = stackalloc byte[inputByteLength];
            _ = Encoding.UTF8.GetBytes(name, input[NamespaceByteLength..]);
            return CreateVersion5(namespaceId, input, nameByteLength);
        }

        var rentedInput = ArrayPool<byte>.Shared.Rent(inputByteLength);

        try
        {
            var input = rentedInput.AsSpan(0, inputByteLength);
            _ = Encoding.UTF8.GetBytes(name, input[NamespaceByteLength..]);
            return CreateVersion5(namespaceId, input, nameByteLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rentedInput.AsSpan(0, inputByteLength));
            ArrayPool<byte>.Shared.Return(rentedInput);
        }
    }

    /// <summary>
    /// Creates a deterministic name-based UUID version 5 using arbitrary name bytes.
    /// </summary>
    /// <param name="namespaceId">The namespace UUID.</param>
    /// <param name="name">The name bytes within the namespace.</param>
    /// <returns>The deterministic UUID.</returns>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 9562 requires SHA-1 for UUID version 5 generation.")]
    public static Uuid CreateVersion5(Uuid namespaceId, ReadOnlySpan<byte> name)
    {
        var inputByteLength = checked(NamespaceByteLength + name.Length);

        if (inputByteLength <= StackAllocationThreshold)
        {
            Span<byte> input = stackalloc byte[inputByteLength];
            name.CopyTo(input[NamespaceByteLength..]);
            return CreateVersion5(namespaceId, input, name.Length);
        }

        var rentedInput = ArrayPool<byte>.Shared.Rent(inputByteLength);

        try
        {
            var input = rentedInput.AsSpan(0, inputByteLength);
            name.CopyTo(input[NamespaceByteLength..]);
            return CreateVersion5(namespaceId, input, name.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rentedInput.AsSpan(0, inputByteLength));
            ArrayPool<byte>.Shared.Return(rentedInput);
        }
    }

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 9562 requires SHA-1 for UUID version 5 generation.")]
    static Uuid CreateVersion5(Uuid namespaceId, Span<byte> input, int nameByteLength)
    {
        _ = namespaceId._value.TryWriteBytes(input, bigEndian: true, out var bytesWritten);
        var inputByteLength = bytesWritten + nameByteLength;
        Span<byte> hash = stackalloc byte[HashByteLength];
        _ = SHA1.TryHashData(input[..inputByteLength], hash, out _);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Uuid(new Guid(hash[..NamespaceByteLength], bigEndian: true));
    }

    /// <summary>
    /// Creates a UUID from its canonical string representation.
    /// </summary>
    /// <param name="value">The UUID text to parse.</param>
    /// <returns>The parsed UUID.</returns>
    public static Uuid Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Uuid(Guid.Parse(value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Creates a UUID from text using the supplied format provider.
    /// </summary>
    /// <param name="s">The UUID text to parse.</param>
    /// <param name="provider">The format provider. UUID parsing is culture-independent.</param>
    /// <returns>The parsed UUID.</returns>
    public static Uuid Parse(string s, IFormatProvider? provider) =>
        new(Guid.Parse(s, provider));

    /// <summary>
    /// Creates a UUID from a span of text using the supplied format provider.
    /// </summary>
    /// <param name="s">The UUID text to parse.</param>
    /// <param name="provider">The format provider. UUID parsing is culture-independent.</param>
    /// <returns>The parsed UUID.</returns>
    public static Uuid Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        new(Guid.Parse(s, provider));

    /// <summary>
    /// Attempts to create a UUID from its canonical string representation.
    /// </summary>
    /// <param name="value">The UUID text to parse.</param>
    /// <param name="uuid">The parsed UUID when this method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> when parsing succeeds; otherwise, <see langword="false" />.</returns>
    public static bool TryParse(string? value, out Uuid uuid)
    {
        if (Guid.TryParse(value, out var parsed))
        {
            uuid = new Uuid(parsed);
            return true;
        }

        uuid = Empty;
        return false;
    }

    /// <summary>
    /// Attempts to create a UUID from text using the supplied format provider.
    /// </summary>
    /// <param name="s">The UUID text to parse.</param>
    /// <param name="provider">The format provider. UUID parsing is culture-independent.</param>
    /// <param name="result">The parsed UUID when this method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> when parsing succeeds; otherwise, <see langword="false" />.</returns>
    public static bool TryParse(string? s, IFormatProvider? provider, out Uuid result)
    {
        if (Guid.TryParse(s, provider, out var parsed))
        {
            result = new Uuid(parsed);
            return true;
        }

        result = Empty;
        return false;
    }

    /// <summary>
    /// Attempts to create a UUID from a span of text using the supplied format provider.
    /// </summary>
    /// <param name="s">The UUID text to parse.</param>
    /// <param name="provider">The format provider. UUID parsing is culture-independent.</param>
    /// <param name="result">The parsed UUID when this method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> when parsing succeeds; otherwise, <see langword="false" />.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Uuid result)
    {
        if (Guid.TryParse(s, provider, out var parsed))
        {
            result = new Uuid(parsed);
            return true;
        }

        result = Empty;
        return false;
    }

    /// <summary>
    /// Creates a UUID from a .NET <see cref="Guid" /> value.
    /// </summary>
    /// <param name="value">The value to wrap.</param>
    /// <returns>The corresponding UUID.</returns>
    public static Uuid FromGuid(Guid value) => new(value);

    /// <summary>
    /// Converts this UUID to a .NET <see cref="Guid" /> value.
    /// </summary>
    /// <returns>The corresponding <see cref="Guid" />.</returns>
    public Guid ToGuid() => _value;

    /// <summary>
    /// Returns the canonical lowercase UUID string.
    /// </summary>
    /// <returns>The canonical UUID string.</returns>
    public override string ToString() => _value.ToString("D");

    /// <summary>Formats this UUID using a standard <see cref="Guid" /> format.</summary>
    /// <param name="format">The standard UUID format, or <see langword="null" /> for the default.</param>
    /// <param name="formatProvider">The format provider. UUID formatting is culture-independent.</param>
    /// <returns>The formatted UUID.</returns>
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        _value.ToString(format, formatProvider);

    /// <summary>Attempts to format this UUID into the supplied destination.</summary>
    /// <param name="destination">The span that receives the formatted UUID.</param>
    /// <param name="charsWritten">The number of characters written when formatting succeeds.</param>
    /// <param name="format">The standard UUID format, or an empty span for the default.</param>
    /// <param name="provider">The format provider. UUID formatting is culture-independent.</param>
    /// <returns><see langword="true" /> when the destination was large enough; otherwise, <see langword="false" />.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        _ = provider;
        return _value.TryFormat(destination, out charsWritten, format);
    }

    /// <summary>Compares this UUID with another using <see cref="Guid" /> ordering.</summary>
    /// <param name="other">The UUID to compare with this value.</param>
    /// <returns>A value indicating the relative order of the UUIDs.</returns>
    public int CompareTo(Uuid other) => _value.CompareTo(other._value);
}

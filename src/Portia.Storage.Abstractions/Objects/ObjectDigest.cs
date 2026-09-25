namespace Cntryl.Portia.Storage;

/// <summary>A lowercase hexadecimal SHA-256 digest and the expected content length.</summary>
public sealed record ObjectDigest
{
    /// <summary>Creates a digest descriptor.</summary>
    /// <param name="sha256">The 64-character lowercase or uppercase hexadecimal SHA-256 value.</param>
    /// <param name="length">The object length in bytes, between zero and 256 MiB.</param>
    public ObjectDigest(string sha256, long length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", nameof(sha256));
        }

        if (length < 0 || length > ObjectStorageLimits.MaximumObjectLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Object length must be between zero and 256 MiB.");
        }

        Sha256 = sha256.ToLowerInvariant();
        Length = length;
    }

    /// <summary>Gets the lowercase hexadecimal SHA-256 digest.</summary>
    public string Sha256 { get; }

    /// <summary>Gets the expected content length in bytes.</summary>
    public long Length { get; }
}

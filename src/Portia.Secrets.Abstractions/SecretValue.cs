using System;
using System.Security.Cryptography;
using System.Threading;

namespace Cntryl.Portia.Secrets;

/// <summary>Owns the bytes read from one version of a secret.</summary>
public sealed class SecretValue : IDisposable
{
    byte[]? _bytes;

    /// <summary>Creates a value and takes ownership of its byte array.</summary>
    /// <param name="versionId">The nonempty, opaque provider version identifier.</param>
    /// <param name="bytes">The payload bytes. The caller must relinquish its use of this array.</param>
    public SecretValue(string versionId, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentNullException.ThrowIfNull(bytes);
        VersionId = versionId;
        _bytes = bytes;
    }

    /// <summary>Gets the version identifier. Compare it for equality within the same secret.</summary>
    public string VersionId { get; }

    /// <summary>Gets the payload bytes. Finish using all views before disposing this value.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes ?? throw new ObjectDisposedException(nameof(SecretValue));

    /// <summary>Clears the owned bytes.</summary>
    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);

        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }
}

using System;
using System.Security.Cryptography;
using System.Threading;

namespace Cntryl.Portia.DataProtection;

/// <summary>Owns authenticated plaintext and the key status observed during decryption.</summary>
public sealed class UnprotectedPayload : IDisposable
{
    byte[]? _plaintext;

    /// <summary>Transfers ownership of a plaintext buffer to this disposable result.</summary>
    public UnprotectedPayload(byte[] plaintext, string keyId, bool needsRotation)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        _plaintext = plaintext;
        KeyId = keyId;
        NeedsRotation = needsRotation;
    }

    /// <summary>Gets the plaintext. Consume it before disposing this result.</summary>
    public ReadOnlyMemory<byte> Plaintext => _plaintext ?? throw new ObjectDisposedException(nameof(UnprotectedPayload));

    /// <summary>Gets the key ID authenticated by the payload.</summary>
    public string KeyId { get; }

    /// <summary>Gets whether the payload was protected with a retained key in the acquired snapshot.</summary>
    public bool NeedsRotation { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        var plaintext = Interlocked.Exchange(ref _plaintext, null);
        if (plaintext is not null)
            CryptographicOperations.ZeroMemory(plaintext);
    }
}

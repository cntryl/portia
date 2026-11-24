using System;
using System.Security.Cryptography;
using System.Threading;

namespace Cntryl.Portia.DataProtection;

/// <summary>Owns a copy of a root and its status within a single key ring snapshot.</summary>
public sealed class DataProtectionKeyLease : IDisposable
{
    byte[]? _rootKey;

    /// <summary>Copies a 32-byte root into an operation-scoped lease.</summary>
    public DataProtectionKeyLease(string keyId, DataProtectionKeyRole role, bool isActive, ReadOnlySpan<byte> rootKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);

        if (rootKey.Length != 32)
            throw new ArgumentException("A data protection root must contain 32 bytes.", nameof(rootKey));

        if (role is not (DataProtectionKeyRole.Protection or DataProtectionKeyRole.Index))
            throw new ArgumentOutOfRangeException(nameof(role));

        if (isActive && role != DataProtectionKeyRole.Protection)
            throw new ArgumentException("Only protection roots can be active.", nameof(isActive));


        KeyId = keyId;
        Role = role;
        IsActive = isActive;
        _rootKey = rootKey.ToArray();
    }

    /// <summary>Gets the key's stable, provider-independent ID.</summary>
    public string KeyId { get; }

    /// <summary>Gets the purpose of the root in this ring.</summary>
    public DataProtectionKeyRole Role { get; }

    /// <summary>Gets whether this was the active protection key in the acquired snapshot.</summary>
    public bool IsActive { get; }

    /// <summary>Gets the leased root. Use it only for the lifetime of this lease.</summary>
    public ReadOnlySpan<byte> RootKey => _rootKey ?? throw new ObjectDisposedException(nameof(DataProtectionKeyLease));

    /// <inheritdoc />
    public void Dispose()
    {
        var root = Interlocked.Exchange(ref _rootKey, null);

        if (root is not null)
            CryptographicOperations.ZeroMemory(root);
    }
}

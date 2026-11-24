using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cntryl.Portia.DataProtection;

/// <summary>Protects sensitive payloads with authenticated, caller-supplied context.</summary>
public interface IPayloadProtector
{
    /// <summary>Protects a payload under the active key. The caller owns the input buffers.</summary>
    ValueTask<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose, ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default);

    /// <summary>Authenticates a payload and returns plaintext that the caller must dispose.</summary>
    ValueTask<UnprotectedPayload> UnprotectAsync(ReadOnlyMemory<byte> payload, string purpose, ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates rotating verification hashes and pinned identifier indexes.</summary>
public interface IKeyedHasher
{
    /// <summary>Creates a verification hash under the active protection key.</summary>
    ValueTask<byte[]> CreateVerificationHashAsync(ReadOnlyMemory<byte> value, string purpose, ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default);

    /// <summary>Checks a candidate against a previously created verification hash.</summary>
    ValueTask<bool> VerifyAsync(ReadOnlyMemory<byte> verificationHash, ReadOnlyMemory<byte> candidate, string purpose,
        ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default);

    /// <summary>Computes a deterministic identifier index under an explicitly pinned index key.</summary>
    ValueTask<byte[]> ComputeIndexHashAsync(ReadOnlyMemory<byte> identifier, string purpose, ReadOnlyMemory<byte> associatedData,
        string indexKeyId, CancellationToken cancellationToken = default);
}

/// <summary>Distinguishes rotating protection roots from independently provisioned index roots.</summary>
public enum DataProtectionKeyRole
{
    /// <summary>Root used for payload protection and verification hashes.</summary>
    Protection,
    /// <summary>Root used for pinned identifier indexes.</summary>
    Index,
}

/// <summary>Provides operation-scoped access to versioned data protection roots.</summary>
public interface IDataProtectionKeyRing
{
    /// <summary>Acquires the active protection root.</summary>
    ValueTask<DataProtectionKeyLease> AcquireActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Acquires the exact root and role, or returns null when absent from the current snapshot.</summary>
    ValueTask<DataProtectionKeyLease?> AcquireAsync(string keyId, DataProtectionKeyRole role,
        CancellationToken cancellationToken = default);
}

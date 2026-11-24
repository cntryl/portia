using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cntryl.Portia.DataProtection;

sealed class DataProtectionCrypto(IDataProtectionKeyRing keyRing) : IPayloadProtector, IKeyedHasher
{
    const int MaxInput = 1024 * 1024;
    const int MaxContext = 4096;
    static readonly UTF8Encoding Utf8 = new(false, true);

    public async ValueTask<byte[]> ProtectAsync(
        ReadOnlyMemory<byte> plaintext,
        string purpose,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default
    )
    {
        Validate(plaintext.Length, purpose, associatedData.Length);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = await Active(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var header = Header(1, lease.KeyId);
        var aad = Aad(header, associatedData.Span);
        var key = Derive(lease.RootKey, 1, purpose);
        var result = new byte[header.Length + 12 + plaintext.Length + 16];
        header.CopyTo(result, 0);

        try
        {
            var nonce = result.AsSpan(header.Length, 12);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext.Span, result.AsSpan(header.Length + 12, plaintext.Length), result.AsSpan(result.Length - 16), aad);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(aad); }
    }

    public async ValueTask<UnprotectedPayload> UnprotectAsync(
        ReadOnlyMemory<byte> payload,
        string purpose,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default
    )
    {
        Validate(0, purpose, associatedData.Length);
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = Parse(payload.Span, 1);
        var length = payload.Length - parsed.Length - 28;

        if (length < 0 || length > MaxInput)
            throw Invalid();

        using var lease = await Required(parsed.Id, DataProtectionKeyRole.Protection, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var aad = Aad(payload.Span[..parsed.Length], associatedData.Span);
        var key = Derive(lease.RootKey, 1, purpose);
        var plaintext = new byte[length];

        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.Span.Slice(parsed.Length, 12), payload.Span.Slice(parsed.Length + 12, length), payload.Span[^16..], plaintext, aad);
            return new UnprotectedPayload(plaintext, lease.KeyId, !lease.IsActive);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw Invalid();
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(aad); }
    }

    public async ValueTask<byte[]> CreateVerificationHashAsync(
        ReadOnlyMemory<byte> value,
        string purpose,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default
    )
    {
        Validate(value.Length, purpose, associatedData.Length);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = await Active(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Hash(2, lease, value.Span, purpose, associatedData.Span);
    }

    public async ValueTask<bool> VerifyAsync(
        ReadOnlyMemory<byte> verificationHash,
        ReadOnlyMemory<byte> candidate,
        string purpose,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default
    )
    {
        Validate(candidate.Length, purpose, associatedData.Length);
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = Parse(verificationHash.Span, 2);

        if (verificationHash.Length != parsed.Length + 32)
            throw Invalid();

        using var lease = await Required(parsed.Id, DataProtectionKeyRole.Protection, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> expected = stackalloc byte[32];

        try
        {
            ComputeDigest(2, lease, verificationHash.Span[..parsed.Length], candidate.Span,
                purpose, associatedData.Span, expected);
            return CryptographicOperations.FixedTimeEquals(expected, verificationHash.Span[parsed.Length..]);
        }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    public async ValueTask<byte[]> ComputeIndexHashAsync(
        ReadOnlyMemory<byte> identifier,
        string purpose,
        ReadOnlyMemory<byte> associatedData,
        string indexKeyId,
        CancellationToken cancellationToken = default
    )
    {
        Validate(identifier.Length, purpose, associatedData.Length);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = await Required(indexKeyId, DataProtectionKeyRole.Index, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Hash(3, lease, identifier.Span, purpose, associatedData.Span);
    }

    static byte[] Hash(byte kind, DataProtectionKeyLease lease, ReadOnlySpan<byte> value, string purpose, ReadOnlySpan<byte> context)
    {
        var header = Header(kind, lease.KeyId);
        var artifact = new byte[header.Length + 32];
        header.CopyTo(artifact, 0);
        ComputeDigest(kind, lease, header, value, purpose, context, artifact.AsSpan(header.Length));
        return artifact;
    }

    static void ComputeDigest(
        byte kind,
        DataProtectionKeyLease lease,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> value,
        string purpose,
        ReadOnlySpan<byte> context,
        Span<byte> digest
    )
    {
        var aad = Aad(header, context);
        var key = Derive(lease.RootKey, kind, purpose);

        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hmac.AppendData(aad);
            hmac.AppendData(value);
            hmac.GetHashAndReset(digest);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(aad); }
    }

    static byte[] Derive(ReadOnlySpan<byte> root, byte kind, string purpose)
    {
        byte[] purposeBytes;

        try
        { purposeBytes = Utf8.GetBytes(purpose); }
        catch (EncoderFallbackException) { throw new ArgumentException("Purpose must be valid UTF-8.", nameof(purpose)); }

        if (purposeBytes.Length is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(purpose));

        var info = new byte["Portia.DataProtection"u8.Length + 4 + purposeBytes.Length];
        "Portia.DataProtection"u8.CopyTo(info);
        var offset = "Portia.DataProtection"u8.Length;
        info[offset] = 1;
        info[offset + 1] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(offset + 2), (ushort)purposeBytes.Length);
        purposeBytes.CopyTo(info, offset + 4);

        try
        {
            var output = new byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, root, output, salt: default, info: info);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(info);
            CryptographicOperations.ZeroMemory(purposeBytes);
        }
    }

    static byte[] Aad(ReadOnlySpan<byte> header, ReadOnlySpan<byte> context)
    {
        if (context.Length is < 1 or > MaxContext)
            throw new ArgumentOutOfRangeException(nameof(context));

        var bytes = new byte[header.Length + 4 + context.Length];
        header.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(header.Length), (uint)context.Length);
        context.CopyTo(bytes.AsSpan(header.Length + 4));
        return bytes;
    }

    static byte[] Header(byte kind, string id)
    {
        if (!DataProtectionKeyIds.IsValid(id))
            throw new DataProtectionKeyUnavailableException();

        var result = new byte[6 + id.Length];
        result[0] = (byte)'P';
        result[1] = (byte)'D';
        result[2] = (byte)'P';
        result[3] = 1;
        result[4] = kind;
        result[5] = (byte)id.Length;
        Encoding.ASCII.GetBytes(id, result.AsSpan(6));
        return result;
    }

    static (int Length, string Id) Parse(ReadOnlySpan<byte> bytes, byte kind)
    {
        if (bytes.Length < 6 || bytes[0] != (byte)'P' || bytes[1] != (byte)'D' || bytes[2] != (byte)'P' || bytes[3] != 1 || bytes[4] != kind)
            throw Invalid();

        var idLength = bytes[5];

        if (idLength is 0 or > DataProtectionKeyIds.MaxLength || bytes.Length < 6 + idLength)
            throw Invalid();

        var id = Encoding.ASCII.GetString(bytes.Slice(6, idLength));

        if (!DataProtectionKeyIds.IsValid(id))
            throw Invalid();

        return (6 + idLength, id);
    }

    static void Validate(int length, string purpose, int contextLength)
    {
        ArgumentNullException.ThrowIfNull(purpose);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxInput);

        if (contextLength is < 1 or > MaxContext)
            throw new ArgumentOutOfRangeException(nameof(contextLength));

        if (purpose.Length is < 1 or > 256 || Utf8.GetByteCount(purpose) > 256)
            throw new ArgumentOutOfRangeException(nameof(purpose));
    }

    static CryptographicException Invalid() => new("The data protection artifact is invalid.");

    async ValueTask<DataProtectionKeyLease> Active(CancellationToken ct)
    {
        DataProtectionKeyLease lease;

        try
        { lease = await keyRing.AcquireActiveAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (DataProtectionKeyUnavailableException) { throw; }
        catch { throw new DataProtectionKeyUnavailableException(); }

        if (ct.IsCancellationRequested)
        {
            lease.Dispose();
            ct.ThrowIfCancellationRequested();
        }

        if (lease.Role == DataProtectionKeyRole.Protection && lease.IsActive)
            return lease;

        lease.Dispose();
        throw new DataProtectionKeyUnavailableException();
    }

    async ValueTask<DataProtectionKeyLease> Required(string id, DataProtectionKeyRole role, CancellationToken ct)
    {
        DataProtectionKeyLease? lease;

        try
        { lease = await keyRing.AcquireAsync(id, role, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (DataProtectionKeyUnavailableException) { throw; }
        catch { throw new DataProtectionKeyUnavailableException(); }

        if (lease is null)
            throw Invalid();

        if (ct.IsCancellationRequested)
        {
            lease.Dispose();
            ct.ThrowIfCancellationRequested();
        }

        if (lease.KeyId == id && lease.Role == role)
            return lease;

        lease.Dispose();
        throw Invalid();
    }
}

namespace Cntryl.Portia.Testing;

/// <summary>Reusable tenant isolation, immutable completion, and bounded delivery checks.</summary>
public static class ObjectStorageConformance
{
    /// <summary>Verifies the core object storage behavior against an isolated provider target.</summary>
    /// <param name="storage">The provider under test.</param>
    /// <param name="tenant">The tenant that owns the test object.</param>
    /// <param name="otherTenant">A different tenant used to check isolation.</param>
    /// <param name="uploadPart">Uploads bytes to the signed part and returns its opaque receipt.</param>
    /// <param name="ct">A token that can cancel verification.</param>
    /// <exception cref="ConformanceViolationException">The provider violates an object storage invariant.</exception>
    public static async ValueTask VerifyAsync(
        IObjectStorage storage,
        TenantId tenant,
        TenantId otherTenant,
        Func<ObjectUploadPart, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ObjectPartReceipt>> uploadPart,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(uploadPart);
        if (tenant == otherTenant)
        {
            throw new ArgumentException("The isolation probe requires two different tenants.", nameof(otherTenant));
        }

        var content = new byte[1024];
        RandomNumberGenerator.Fill(content);
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        try
        {
            await CompleteAsync(storage, tenant, digest, content, uploadPart, ct).ConfigureAwait(false);
            if (!await storage.HeadAndVerifyAsync(tenant, digest, ct).ConfigureAwait(false))
            {
                throw new ConformanceViolationException("A completed object could not be verified in its tenant.");
            }

            if (await storage.HeadAndVerifyAsync(otherTenant, digest, ct).ConfigureAwait(false))
            {
                throw new ConformanceViolationException("A tenant could read another tenant's object by digest.");
            }

            await CompleteAsync(storage, tenant, digest, content, uploadPart, ct).ConfigureAwait(false);
            await CompleteAsync(storage, otherTenant, digest, content, uploadPart, ct).ConfigureAwait(false);
            if (!await storage.HeadAndVerifyAsync(otherTenant, digest, ct).ConfigureAwait(false))
            {
                throw new ConformanceViolationException("The same digest could not be stored independently for another tenant.");
            }

            var download = await storage.CreateDownloadAsync(tenant, digest, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (download.ExpiresAt <= now || download.ExpiresAt > now.Add(ObjectStorageLimits.MaximumDownloadLifetime))
            {
                throw new ConformanceViolationException("The provider returned a download link outside the bounded lifetime.");
            }

            await using var stream = await storage.OpenReadAsync(tenant, digest, ct).ConfigureAwait(false);
            if (stream is null)
            {
                throw new ConformanceViolationException("A completed object could not be opened for reading.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[256];
            long length = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
                hash.AppendData(buffer, 0, read);
            }

            if (length != digest.Length ||
                !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(digest.Sha256)))
            {
                throw new ConformanceViolationException("The provider returned bytes that did not match the verified object digest.");
            }
        }
        finally
        {
            await storage.DeleteAsync(tenant, digest, CancellationToken.None).ConfigureAwait(false);
            await storage.DeleteAsync(otherTenant, digest, CancellationToken.None).ConfigureAwait(false);
        }
    }

    static async ValueTask CompleteAsync(
        IObjectStorage storage,
        TenantId tenant,
        ObjectDigest digest,
        ReadOnlyMemory<byte> content,
        Func<ObjectUploadPart, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ObjectPartReceipt>> uploadPart,
        CancellationToken ct)
    {
        var session = await storage.CreateUploadAsync(tenant, digest, ct).ConfigureAwait(false);
        try
        {
            var signedPart = await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            var receipt = await uploadPart(signedPart, content, ct).ConfigureAwait(false);
            await storage.CompleteUploadAsync(session, [receipt], ct).ConfigureAwait(false);
        }
        catch
        {
            await storage.AbortUploadAsync(session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

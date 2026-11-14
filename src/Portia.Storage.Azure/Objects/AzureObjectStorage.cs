using System.Globalization;
using System.Text;

namespace Cntryl.Portia.Storage.Azure;

/// <summary>Implements immutable tenant-scoped objects with Azure Blob Storage block blobs.</summary>
public sealed class AzureObjectStorage(BlobServiceClient client, IObjectStorageTenantResolver tenantResolver, IDataProtector sessionProtector) : IObjectStorage
{
    // Azure metadata names must be valid C# identifiers, so they cannot contain hyphens.
    const string DigestMetadata = "portiasha256";
    const string LengthMetadata = "portialength";
    const int MaximumParts = 10_000;
    static readonly TimeSpan UploadLifetime = TimeSpan.FromHours(24);
    static readonly TimeSpan SourceReadLifetime = TimeSpan.FromMinutes(10);
    static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    static readonly TimeSpan DelegationKeyLifetime = TimeSpan.FromHours(1);

    readonly BlobServiceClient _client = client ?? throw new ArgumentNullException(nameof(client));
    readonly IObjectStorageTenantResolver _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
    readonly IDataProtector _sessionProtector = sessionProtector ?? throw new ArgumentNullException(nameof(sessionProtector));
    // Racing refreshes only cost a duplicate key request, so the cache needs no lock.
    volatile UserDelegationKey? _delegationKey;

    /// <inheritdoc />
    public async ValueTask<ObjectUploadSession> CreateUploadAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        var objectKey = ResolveObjectKey(tenantId, expected);
        var uploadKey = ResolveUploadKey(tenantId, objectKey);

        // A block blob needs no server-side initiation: blocks stay uncommitted under the unique staging
        // name until completion commits them, so the session is only this protected token.
        return new(new SessionPayload(
            location.BucketReference,
            location.EncryptionKeyReference,
            objectKey,
            uploadKey,
            expected.Sha256,
            expected.Length,
            DateTimeOffset.UtcNow.Add(UploadLifetime)).Encode(_sessionProtector));
    }

    /// <inheritdoc />
    public async ValueTask<ObjectUploadPart> SignPartAsync(ObjectUploadSession session, int partNumber, TimeSpan lifetime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        if (partNumber is < 1 or > MaximumParts)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        var duration = BoundLifetime(lifetime);
        var payload = SessionPayload.Decode(session.Token, _sessionProtector);
        EnsureSessionActive(payload);
        var expiresAt = DateTimeOffset.UtcNow.Add(duration);
        if (expiresAt > payload.ExpiresAt)
        {
            expiresAt = payload.ExpiresAt;
        }

        // The SAS is pinned to the tenant's encryption scope, so the uploaded block cannot be written
        // under a different scope. Put Block needs write permission on exactly this staging blob.
        var blob = _client.GetBlobContainerClient(payload.Container).GetBlobClient(payload.StagingKey);
        var signed = await SignAsync(blob, BlobSasPermissions.Write, expiresAt, payload.EncryptionScope, ct).ConfigureAwait(false);
        var blockId = BlockId(partNumber);
        var uri = new Uri($"{signed.AbsoluteUri}&comp=block&blockid={Uri.EscapeDataString(blockId)}");
        return new ObjectUploadPart(partNumber, uri, expiresAt);
    }

    /// <inheritdoc />
    public async ValueTask CompleteUploadAsync(ObjectUploadSession session, IReadOnlyList<ObjectPartReceipt> parts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(parts);
        var payload = SessionPayload.Decode(session.Token, _sessionProtector);
        EnsureSessionActive(payload);
        // A canceled call can be a retry after Azure committed completion but lost its response.
        // Only an explicit abort can safely discard its potentially committed staging blob.
        ct.ThrowIfCancellationRequested();

        if (parts.Count is < 1 or > MaximumParts || parts.Any(static part => part is null || string.IsNullOrWhiteSpace(part.Token)) ||
            parts.Select(static part => part.PartNumber).Distinct().Count() != parts.Count ||
            parts.Any(static part => part.PartNumber is < 1 or > MaximumParts))
        {
            throw new ArgumentException("Block completion requires unique, valid part receipts.", nameof(parts));
        }

        var ordered = parts.OrderBy(static part => part.PartNumber).ToArray();
        var staging = StagingBlock(payload);

        BlockList blocks;
        try
        {
            blocks = (await staging.GetBlockListAsync(BlockListTypes.All, cancellationToken: ct).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // Staging is gone after a successful promotion whose response was lost, or was never uploaded.
            if (await HeadAndVerifyAsync(payload.Container, payload.ObjectKey, payload.Sha256, payload.Length, ct).ConfigureAwait(false))
            {
                return;
            }

            throw new InvalidDataException("No uploaded blocks were found for this upload session.", exception);
        }

        var blockIds = ValidateReceipts(ordered, blocks, out var totalLength);
        if (totalLength > ObjectStorageLimits.MaximumObjectLength || totalLength != payload.Length)
        {
            // The uploaded blocks can never match the declared content, so the session ends.
            await TryCleanupStagingAsync(payload).ConfigureAwait(false);
            throw new InvalidDataException("Uploaded object length does not match the declared content.");
        }

        if (!AlreadyCommitted(blockIds, blocks))
        {
            try
            {
                await staging.CommitBlockListAsync(blockIds, new CommitBlockListOptions
                {
                    Metadata = ContentMetadata(payload.Sha256, payload.Length),
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
                }, ct).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (exception.ErrorCode == "InvalidBlockList" || exception.Status == 412)
            {
                // A concurrent completion consumed the uncommitted blocks first.
                if (await HeadAndVerifyAsync(payload.Container, payload.ObjectKey, payload.Sha256, payload.Length, ct).ConfigureAwait(false))
                {
                    return;
                }

                // A committed staging blob is immutable: another completion may still be
                // verifying it. Verify that blob below instead of replacing it and changing its ETag.
                if (exception.Status != 412)
                    throw new InvalidDataException("Azure rejected the uploaded blocks or their receipts.", exception);
            }
        }

        (string Digest, ETag ETag) verified;
        try
        {
            verified = await VerifyStagingAsync(payload, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await TryCleanupStagingAsync(payload).ConfigureAwait(false);
            throw;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // A concurrent completion may have promoted and removed staging while this caller
            // was retrying. An absent destination leaves the outcome unresolved for another retry.
            if (await HeadAndVerifyAsync(payload.Container, payload.ObjectKey, payload.Sha256, payload.Length, ct).ConfigureAwait(false))
            {
                return;
            }

            throw;
        }

        await PromoteVerifiedAsync(payload, verified.Digest, verified.ETag, ct).ConfigureAwait(false);
        await TryCleanupStagingAsync(payload).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AbortUploadAsync(ObjectUploadSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var payload = SessionPayload.Decode(session.Token, _sessionProtector);

        // A failed cleanup propagates unchanged; the caller retries the abort.
        await CleanupStagingAsync(payload, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> HeadAndVerifyAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        var objectKey = ResolveObjectKey(tenantId, expected);
        return await HeadAndVerifyAsync(location.BucketReference, objectKey, expected.Sha256, expected.Length, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ObjectDownload> CreateDownloadAsync(TenantId tenantId, ObjectDigest expected, TimeSpan lifetime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var duration = BoundLifetime(lifetime);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        var objectKey = ResolveObjectKey(tenantId, expected);
        if (!await HeadAndVerifyAsync(location.BucketReference, objectKey, expected.Sha256, expected.Length, ct).ConfigureAwait(false))
        {
            throw new FileNotFoundException("The verified object was not found.");
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(duration);
        var blob = _client.GetBlobContainerClient(location.BucketReference).GetBlobClient(objectKey);
        var url = await SignAsync(blob, BlobSasPermissions.Read, expiresAt, encryptionScope: null, ct).ConfigureAwait(false);
        return new(url, expiresAt);
    }

    /// <inheritdoc />
    public async ValueTask<Stream?> OpenReadAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        var objectKey = ResolveObjectKey(tenantId, expected);
        var verified = await GetVerifiedAsync(location.BucketReference, objectKey, expected.Sha256, expected.Length, ct).ConfigureAwait(false);
        if (verified is null)
        {
            return null;
        }

        try
        {
            var blob = _client.GetBlobContainerClient(location.BucketReference).GetBlobClient(objectKey);
            // The metadata read is advisory; the conditional GET binds these bytes to that verified revision.
            var response = await blob.DownloadStreamingAsync(new BlobDownloadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = verified.ETag }
            }, ct).ConfigureAwait(false);
            return new DownloadStream(response.Value);
        }
        catch (RequestFailedException exception) when (exception.Status is 404 or 412)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        var objectKey = ResolveObjectKey(tenantId, expected);
        await _client.GetBlobContainerClient(location.BucketReference).GetBlobClient(objectKey)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct).ConfigureAwait(false);
    }

    async ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct)
    {
        var location = await _tenantResolver.ResolveAsync(tenantId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(location.BucketReference) || string.IsNullOrWhiteSpace(location.EncryptionKeyReference))
        {
            throw new InvalidOperationException("Tenant storage resolution must provide container and encryption-scope references.");
        }

        return location;
    }

    /// <summary>A block blob client for the staging blob that applies the tenant's encryption scope to every write.</summary>
    BlockBlobClient StagingBlock(SessionPayload payload) =>
        _client.GetBlobContainerClient(payload.Container).GetBlockBlobClient(payload.StagingKey)
            .WithEncryptionScope(payload.EncryptionScope);

    static string[] ValidateReceipts(ObjectPartReceipt[] ordered, BlockList blocks, out long totalLength)
    {
        var uncommitted = blocks.UncommittedBlocks.ToDictionary(static block => block.Name, static block => block.SizeLong, StringComparer.Ordinal);
        var committed = blocks.CommittedBlocks.ToDictionary(static block => block.Name, static block => block.SizeLong, StringComparer.Ordinal);
        var ids = new string[ordered.Length];
        totalLength = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var id = BlockId(ordered[index].PartNumber);
            if (!string.Equals(ordered[index].Token, id, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A part receipt does not match its part number.");
            }

            // A block re-uploaded after an earlier commit lives in both lists; the uncommitted one is the latest.
            if (!uncommitted.TryGetValue(id, out var size) && !committed.TryGetValue(id, out size))
            {
                throw new InvalidDataException("A part receipt refers to a block that was not uploaded.");
            }

            // Azure enforces no minimum block size, so the adapter does, like the other providers.
            if (index < ordered.Length - 1 && size < ObjectStorageLimits.MinimumNonfinalPartLength)
            {
                throw new InvalidDataException("Every nonfinal part must contain at least 5 MiB.");
            }

            ids[index] = id;
            totalLength = checked(totalLength + size);
        }

        return ids;
    }

    /// <summary>True when the blob already holds exactly this committed block list and no newer uncommitted block replaces one.</summary>
    static bool AlreadyCommitted(string[] blockIds, BlockList blocks) =>
        blocks.CommittedBlocks.Select(static block => block.Name).SequenceEqual(blockIds, StringComparer.Ordinal) &&
        !blocks.UncommittedBlocks.Any(block => blockIds.Contains(block.Name, StringComparer.Ordinal));

    async ValueTask<(string Digest, ETag ETag)> VerifyStagingAsync(SessionPayload payload, CancellationToken ct)
    {
        var staging = _client.GetBlobContainerClient(payload.Container).GetBlobClient(payload.StagingKey);
        using var staged = (await staging.DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
        var etag = staged.Details.ETag;
        if (etag == default)
        {
            throw new InvalidOperationException("Azure did not return an ETag for the staged blob.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var read = await staged.Content.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length = checked(length + read);
            if (length > payload.Length)
            {
                throw new InvalidDataException("Uploaded object exceeds its declared length.");
            }

            hash.AppendData(buffer, 0, read);
        }

        var computed = hash.GetHashAndReset();
        if (length != payload.Length || !CryptographicOperations.FixedTimeEquals(computed, Convert.FromHexString(payload.Sha256)))
        {
            throw new InvalidDataException("Uploaded object length or SHA-256 does not match the declared content.");
        }

        return (Convert.ToHexString(computed).ToLowerInvariant(), etag);
    }

    async ValueTask PromoteVerifiedAsync(SessionPayload payload, string verifiedDigest, ETag stagedETag, CancellationToken ct)
    {
        var container = _client.GetBlobContainerClient(payload.Container);
        var source = container.GetBlobClient(payload.StagingKey);
        // Copy Blob From URL is synchronous up to 256 MiB, the object limit, so the destination never
        // becomes visible half-written. The source is addressed by a short-lived read SAS.
        var sourceUri = await SignAsync(source, BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(SourceReadLifetime),
            encryptionScope: null, ct).ConfigureAwait(false);
        var destination = container.GetBlobClient(payload.ObjectKey).WithEncryptionScope(payload.EncryptionScope);
        try
        {
            await destination.SyncCopyFromUriAsync(sourceUri, new BlobCopyFromUriOptions
            {
                Metadata = ContentMetadata(verifiedDigest, payload.Length),
                SourceConditions = new BlobRequestConditions { IfMatch = stagedETag },
                DestinationConditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            }, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412 || exception.Status == 404 && exception.ErrorCode == "CannotVerifyCopySource")
        {
            if (await HeadAndVerifyAsync(payload.Container, payload.ObjectKey, payload.Sha256, payload.Length, ct).ConfigureAwait(false))
            {
                return;
            }

            // The conditional copy cannot say whether the source or the destination condition failed.
            // An unverified blob at the immutable key is corruption; otherwise the staged source changed
            // after verification and the caller may retry the whole completion.
            var existing = await destination.ExistsAsync(ct).ConfigureAwait(false);
            if (existing.Value)
            {
                // The destination may have appeared since the first read.
                if (await HeadAndVerifyAsync(payload.Container, payload.ObjectKey, payload.Sha256, payload.Length, ct).ConfigureAwait(false))
                    return;
                throw new InvalidDataException("An object already exists at the immutable content key but failed verification.", exception);
            }

            throw;
        }
    }

    async ValueTask<bool> HeadAndVerifyAsync(string container, string objectKey, string sha256, long length, CancellationToken ct) =>
        await GetVerifiedAsync(container, objectKey, sha256, length, ct).ConfigureAwait(false) is not null;

    async ValueTask<BlobProperties?> GetVerifiedAsync(string container, string objectKey, string sha256, long length, CancellationToken ct)
    {
        try
        {
            var properties = (await _client.GetBlobContainerClient(container).GetBlobClient(objectKey)
                .GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
            return properties.ContentLength == length &&
                properties.Metadata.TryGetValue(DigestMetadata, out var digest) && string.Equals(digest, sha256, StringComparison.Ordinal)
                ? properties
                : null;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    async ValueTask CleanupStagingAsync(SessionPayload payload, CancellationToken ct)
    {
        var staging = StagingBlock(payload);
        var deleted = await staging.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct).ConfigureAwait(false);
        if (deleted.Value)
        {
            return;
        }

        // Delete Blob cannot see a blob that only has uncommitted blocks. Committing an empty list
        // discards them immediately instead of waiting for Azure's garbage collection.
        try
        {
            var blocks = await staging.GetBlockListAsync(BlockListTypes.Uncommitted, cancellationToken: ct).ConfigureAwait(false);
            if (blocks.Value.UncommittedBlocks.Any())
            {
                await staging.CommitBlockListAsync([], cancellationToken: ct).ConfigureAwait(false);
                await staging.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // Nothing was ever staged, or another cleanup finished first.
        }
    }

    /// <summary>
    /// Removes staging data after the completion outcome is already decided. A cleanup failure must not
    /// replace that outcome; the staging lifecycle rule removes anything left behind.
    /// </summary>
    async ValueTask TryCleanupStagingAsync(SessionPayload payload)
    {
        try
        {
            await CleanupStagingAsync(payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is RequestFailedException or System.Net.Http.HttpRequestException or IOException or
            TimeoutException or TaskCanceledException)
        {
            // Best effort by design: the object is committed (or rejected) and staging expires by lifecycle rule.
        }
    }

    async ValueTask<Uri> SignAsync(BlobClient blob, BlobSasPermissions permissions, DateTimeOffset expiresAt, string? encryptionScope, CancellationToken ct)
    {
        var builder = new BlobSasBuilder(permissions, expiresAt)
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.Subtract(ClockSkew),
            Protocol = blob.Uri.Scheme == Uri.UriSchemeHttp ? SasProtocol.HttpsAndHttp : SasProtocol.Https,
            EncryptionScope = encryptionScope
        };

        if (blob.CanGenerateSasUri)
        {
            return blob.GenerateSasUri(builder);
        }

        // Entra ID (managed identity) clients cannot sign with an account key; sign with a user delegation key.
        var key = await GetDelegationKeyAsync(expiresAt, ct).ConfigureAwait(false);
        var sas = builder.ToSasQueryParameters(key, _client.AccountName);
        return new BlobUriBuilder(blob.Uri) { Sas = sas }.ToUri();
    }

    async ValueTask<UserDelegationKey> GetDelegationKeyAsync(DateTimeOffset needUntil, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_delegationKey is { } cached && cached.SignedExpiresOn >= needUntil.AddMinutes(1) && cached.SignedStartsOn <= now)
        {
            return cached;
        }

        var expires = needUntil > now.Add(DelegationKeyLifetime) ? needUntil.AddMinutes(1) : now.Add(DelegationKeyLifetime);
        try
        {
            var key = (await _client.GetUserDelegationKeyAsync(now.Subtract(ClockSkew), expires, ct).ConfigureAwait(false)).Value;
            _delegationKey = key;
            return key;
        }
        catch (RequestFailedException exception)
        {
            throw new InvalidOperationException(
                "Signing links requires a BlobServiceClient with a shared-key credential, or an Entra ID credential allowed to request a user delegation key.",
                exception);
        }
    }

    static Dictionary<string, string> ContentMetadata(string sha256, long length) => new(StringComparer.Ordinal)
    {
        [DigestMetadata] = sha256,
        [LengthMetadata] = length.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>Gets the fixed-length base64 block name for a part; every block name in one blob must have the same length.</summary>
    static string BlockId(int partNumber) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(partNumber.ToString("D5", CultureInfo.InvariantCulture)));

    static TimeSpan BoundLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > ObjectStorageLimits.MaximumDownloadLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), $"Lifetime must be positive and no longer than {ObjectStorageLimits.MaximumDownloadLifetime}.");
        }

        return lifetime;
    }

    static void EnsureSessionActive(SessionPayload payload)
    {
        if (payload.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The upload session has expired.");
        }
    }

    string ResolveObjectKey(TenantId tenantId, ObjectDigest expected)
    {
        var key = _tenantResolver.GetObjectKey(tenantId, expected);
        return !string.IsNullOrWhiteSpace(key) ? key
            : throw new InvalidOperationException("The application must resolve a nonblank object key.");
    }

    string ResolveUploadKey(TenantId tenantId, string objectKey)
    {
        var key = _tenantResolver.GetUploadKey(tenantId, Uuid.CreateVersion4());
        if (string.IsNullOrWhiteSpace(key) || string.Equals(key, objectKey, StringComparison.Ordinal) ||
            objectKey.StartsWith(key.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("The application must resolve a unique upload key disjoint from the object key.");
        return key;
    }

    internal sealed record SessionPayload(
        string Container,
        string EncryptionScope,
        string ObjectKey,
        string StagingKey,
        string Sha256,
        long Length,
        DateTimeOffset ExpiresAt)
    {
        public string Encode(IDataProtector protector) => protector.Protect(
            JsonSerializer.Serialize(this, AzureUploadSessionJsonContext.Default.SessionPayload));

        public static SessionPayload Decode(string token, IDataProtector protector)
        {
            try
            {
                var json = protector.Unprotect(token);
                var payload = JsonSerializer.Deserialize(json, AzureUploadSessionJsonContext.Default.SessionPayload);
                if (payload is null || string.IsNullOrWhiteSpace(payload.Container) || string.IsNullOrWhiteSpace(payload.EncryptionScope) ||
                    string.IsNullOrWhiteSpace(payload.ObjectKey) || string.IsNullOrWhiteSpace(payload.StagingKey) || string.IsNullOrWhiteSpace(payload.Sha256) ||
                    payload.Sha256.Length != 64 || !payload.Sha256.All(Uri.IsHexDigit) ||
                    payload.Length is < 0 or > ObjectStorageLimits.MaximumObjectLength || payload.ExpiresAt == default)
                {
                    throw new FormatException();
                }

                return payload;
            }
            catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or CryptographicException)
            {
                throw new ArgumentException("The upload session token is invalid.", nameof(token), exception);
            }
        }
    }

    sealed class DownloadStream(BlobDownloadStreamingResult result) : Stream
    {
        readonly BlobDownloadStreamingResult _result = result;
        readonly Stream _inner = result.Content;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _result.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync()
        {
            Dispose();
            return base.DisposeAsync();
        }
    }
}

[JsonSerializable(typeof(AzureObjectStorage.SessionPayload))]
sealed partial class AzureUploadSessionJsonContext : JsonSerializerContext;

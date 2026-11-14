using System.Globalization;
using System.Net;
using System.Net.Http;
using Google;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace Cntryl.Portia.Storage.Gcp;

/// <summary>
/// Implements immutable tenant-scoped objects with Google Cloud Storage. Each part is a separate staging object
/// uploaded through a V4 signed URL, then assembled with server-side compose.
/// </summary>
public sealed class GcpObjectStorage(
    StorageClient client,
    UrlSigner signer,
    IObjectStorageTenantResolver tenantResolver,
    IDataProtector sessionProtector) : IObjectStorage
{
    const string DigestMetadata = "portia-sha256";
    const string LengthMetadata = "portia-length";
    const string ContentType = "application/octet-stream";
    const int MaximumParts = 10_000;
    const int MaximumConditionalCopyAttempts = 3;
    static readonly TimeSpan UploadLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether a bucket default key, staged parts, and the promoted object must report the tenant key. Always on in
    /// production; only tests against emulators that cannot report encryption turn it off.
    /// </summary>
    internal bool VerifyEncryptionEvidence { get; init; } = true;

    readonly StorageClient _client = client ?? throw new ArgumentNullException(nameof(client));
    readonly UrlSigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    readonly IObjectStorageTenantResolver _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
    readonly IDataProtector _sessionProtector = sessionProtector ?? throw new ArgumentNullException(nameof(sessionProtector));

    /// <inheritdoc />
    public async ValueTask<ObjectUploadSession> CreateUploadAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        var objectKey = ResolveObjectKey(tenantId, expected);
        var uploadKey = ResolveUploadKey(tenantId, objectKey);

        // Signed part URLs cannot carry the tenant key, so staged parts are encrypted by the bucket default.
        // Refuse to start an upload that could land under any other key.
        var bucket = await _client.GetBucketAsync(location.BucketReference, cancellationToken: ct).ConfigureAwait(false);
        if (VerifyEncryptionEvidence && !IsKey(bucket.Encryption?.DefaultKmsKeyName, location.EncryptionKeyReference))
        {
            throw new InvalidOperationException("The tenant bucket's default Cloud KMS key must be the tenant encryption key.");
        }

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
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(duration);
        if (expiresAt > payload.ExpiresAt)
        {
            expiresAt = payload.ExpiresAt;
        }

        var uri = await SignAsync(payload.Bucket, PartName(payload.Prefix, partNumber), expiresAt - now,
            HttpMethod.Put, ct).ConfigureAwait(false);
        return new ObjectUploadPart(partNumber, new Uri(uri), expiresAt);
    }

    /// <inheritdoc />
    public async ValueTask CompleteUploadAsync(ObjectUploadSession session, IReadOnlyList<ObjectPartReceipt> parts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(parts);
        var payload = SessionPayload.Decode(session.Token, _sessionProtector);
        EnsureSessionActive(payload);
        // A canceled call can be a retry after completion committed but lost its response.
        // Only an explicit abort can safely discard potentially promoted staging data.
        ct.ThrowIfCancellationRequested();

        if (parts.Count is < 1 or > MaximumParts || parts.Any(static part => part is null || string.IsNullOrWhiteSpace(part.Token)) ||
            parts.Select(static part => part.PartNumber).Distinct().Count() != parts.Count ||
            parts.Any(static part => part.PartNumber is < 1 or > MaximumParts))
        {
            throw new ArgumentException("Multipart completion requires unique, valid part receipts.", nameof(parts));
        }

        var ordered = parts.OrderBy(static part => part.PartNumber).ToArray();
        var sources = new List<Source>(ordered.Length);
        long total = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var part = ordered[index];
            // The receipt is the generation Cloud Storage assigned to the uploaded part (x-goog-generation).
            if (!long.TryParse(part.Token, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) || generation <= 0)
            {
                throw new InvalidDataException("A part receipt is not a Cloud Storage object generation.");
            }

            var name = PartName(payload.Prefix, part.PartNumber);
            var staged = await TryGetObjectAsync(payload.Bucket, name, generation, ct).ConfigureAwait(false);
            if (staged is null)
            {
                // After a successful completion the part objects are gone, so a lost-response retry lands here.
                if (await IsPromotedAsync(payload, ct).ConfigureAwait(false))
                {
                    await TryCleanupStagingAsync(payload).ConfigureAwait(false);
                    return;
                }

                throw new InvalidDataException("An uploaded part is missing or its receipt no longer matches the staged part.");
            }

            if (VerifyEncryptionEvidence)
            {
                RequireTenantKey(staged, payload);
            }

            var size = checked((long)(staged.Size ?? 0));
            if (index < ordered.Length - 1 && size < ObjectStorageLimits.MinimumNonfinalPartLength)
            {
                throw new InvalidDataException("Every nonfinal multipart part must contain at least 5 MiB.");
            }

            total = checked(total + size);
            sources.Add(new Source(name, generation));
        }

        if (total != payload.Length || total > ObjectStorageLimits.MaximumObjectLength)
        {
            await TryCleanupStagingAsync(payload).ConfigureAwait(false);
            throw new InvalidDataException("Uploaded parts do not add up to the declared content length.");
        }

        // The parts are immutable generations, so the bytes verified here are the bytes promoted below.
        string digest;
        try
        {
            digest = await VerifyPartsAsync(payload, sources, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await TryCleanupStagingAsync(payload).ConfigureAwait(false);
            throw;
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // A concurrent completion may have promoted and removed the parts after they were inspected.
            if (await IsPromotedAsync(payload, ct).ConfigureAwait(false))
            {
                await TryCleanupStagingAsync(payload).ConfigureAwait(false);
                return;
            }

            throw new InvalidDataException("An uploaded part changed or disappeared during completion.", exception);
        }

        await PromoteVerifiedAsync(payload, sources, digest, ct).ConfigureAwait(false);
        await TryCleanupStagingAsync(payload).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AbortUploadAsync(ObjectUploadSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var payload = SessionPayload.Decode(session.Token, _sessionProtector);
        // A failed abort propagates unchanged; the caller retries the abort, which then cleans staging.
        await CleanupStagingAsync(payload, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> HeadAndVerifyAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        var objectKey = ResolveObjectKey(tenantId, expected);
        return await GetVerifiedAsync(location.BucketReference, objectKey, expected.Sha256, expected.Length, ct).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public async ValueTask<ObjectDownload> CreateDownloadAsync(TenantId tenantId, ObjectDigest expected, TimeSpan lifetime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var duration = BoundLifetime(lifetime);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        var objectKey = ResolveObjectKey(tenantId, expected);
        if (await GetVerifiedAsync(location.BucketReference, objectKey, expected.Sha256, expected.Length, ct).ConfigureAwait(false) is null)
        {
            throw new FileNotFoundException("The verified object was not found.");
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(duration);
        var url = await SignAsync(location.BucketReference, objectKey, duration,
            HttpMethod.Get, ct).ConfigureAwait(false);
        return new(new Uri(url), expiresAt);
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
            // Pin the generation that was verified so a concurrent replacement cannot be streamed instead.
            var request = _client.Service.Objects.Get(location.BucketReference, objectKey);
            request.Generation = verified.Generation;
            request.Alt = Google.Apis.Storage.v1.StorageBaseServiceRequest<GcsObject>.AltEnum.Media;
            return await request.ExecuteAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
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
        try
        {
            await _client.DeleteObjectAsync(location.BucketReference, objectKey, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Deleting an absent object is already the desired state.
        }
    }

    async ValueTask<string> SignAsync(string bucket, string name, TimeSpan duration, HttpMethod method, CancellationToken ct) =>
        await _signer.SignAsync(bucket, name, duration, method, SigningVersion.V4, ct).ConfigureAwait(false);

    async ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct)
    {
        var location = await _tenantResolver.ResolveAsync(tenantId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(location.BucketReference) || string.IsNullOrWhiteSpace(location.EncryptionKeyReference))
        {
            throw new InvalidOperationException("Tenant storage resolution must provide bucket and encryption-key references.");
        }

        return location;
    }

    /// <summary>Streams the ordered parts once to check the actual length and full SHA-256 of the assembled content.</summary>
    async ValueTask<string> VerifyPartsAsync(SessionPayload payload, List<Source> sources, CancellationToken ct)
    {
        await using var content = new PartsStream(_client, payload.Bucket, sources, ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var read = await content.ReadAsync(buffer, ct).ConfigureAwait(false);
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

        return Convert.ToHexString(computed).ToLowerInvariant();
    }

    /// <summary>
    /// Writes the verified bytes to the immutable content key, tenant-keyed, only if no object exists there. The
    /// object is created in one conditional upload, so it is never visible without its digest metadata.
    /// </summary>
    async ValueTask PromoteVerifiedAsync(SessionPayload payload, List<Source> sources, string verifiedDigest, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaximumConditionalCopyAttempts; attempt++)
        {
            try
            {
                await using var content = new PartsStream(_client, payload.Bucket, sources, ct);
                var promoted = await _client.UploadObjectAsync(
                    new GcsObject
                    {
                        Bucket = payload.Bucket,
                        Name = payload.ObjectKey,
                        ContentType = ContentType,
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [DigestMetadata] = verifiedDigest,
                            [LengthMetadata] = payload.Length.ToString(CultureInfo.InvariantCulture)
                        }
                    },
                    content,
                    new UploadObjectOptions { IfGenerationMatch = 0, KmsKeyName = payload.KmsKey },
                    ct).ConfigureAwait(false);
                if (VerifyEncryptionEvidence)
                {
                    RequireTenantKey(promoted, payload);
                }

                return;
            }
            catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            {
                if (await IsPromotedAsync(payload, ct).ConfigureAwait(false))
                {
                    return;
                }

                if (exception.HttpStatusCode == HttpStatusCode.PreconditionFailed &&
                    await TryGetObjectAsync(payload.Bucket, payload.ObjectKey, null, ct).ConfigureAwait(false) is not null)
                {
                    throw new InvalidDataException("An object already exists at the immutable content key but failed verification.", exception);
                }

                if (exception.HttpStatusCode != HttpStatusCode.Conflict || attempt == MaximumConditionalCopyAttempts)
                {
                    // A vanished part with no promoted destination is unresolved; staging stays for a retry.
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    async ValueTask<bool> IsPromotedAsync(SessionPayload payload, CancellationToken ct)
    {
        var promoted = await GetVerifiedAsync(payload.Bucket, payload.ObjectKey, payload.Sha256, payload.Length, ct).ConfigureAwait(false);
        if (promoted is null)
            return false;

        // A conflict or lost-response retry must meet the same encryption requirement as a new write.
        if (VerifyEncryptionEvidence)
            RequireTenantKey(promoted, payload);
        return true;
    }

    async ValueTask<GcsObject?> GetVerifiedAsync(string bucket, string objectKey, string sha256, long length, CancellationToken ct)
    {
        var content = await TryGetObjectAsync(bucket, objectKey, null, ct).ConfigureAwait(false);
        return content is not null && content.Size == (ulong)length &&
            content.Metadata is not null && content.Metadata.TryGetValue(DigestMetadata, out var digest) &&
            string.Equals(digest, sha256, StringComparison.Ordinal)
            ? content
            : null;
    }

    async ValueTask<GcsObject?> TryGetObjectAsync(string bucket, string name, long? generation, CancellationToken ct)
    {
        try
        {
            return await _client.GetObjectAsync(bucket, name, new GetObjectOptions { Generation = generation }, ct).ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    async ValueTask CleanupStagingAsync(SessionPayload payload, CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var staged in _client.ListObjectsAsync(payload.Bucket, payload.Prefix + "/").WithCancellation(ct).ConfigureAwait(false))
        {
            names.Add(staged.Name);
        }

        foreach (var name in names)
        {
            try
            {
                await _client.DeleteObjectAsync(payload.Bucket, name, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
            {
                // Staging cleanup is idempotent.
            }
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
        catch (Exception exception) when (exception is GoogleApiException or System.Net.Http.HttpRequestException or IOException or
            TimeoutException or TaskCanceledException)
        {
            // Best effort by design: the object is committed (or rejected) and staging expires by lifecycle rule.
        }
    }

    static void RequireTenantKey(GcsObject content, SessionPayload payload)
    {
        if (!IsKey(content.KmsKeyName, payload.KmsKey))
        {
            throw new InvalidOperationException(
                "An object is not encrypted with the tenant's Cloud KMS key; set it as the bucket's default encryption key.");
        }
    }

    /// <summary>Cloud Storage reports an object's key with a trailing <c>/cryptoKeyVersions/N</c> segment.</summary>
    static bool IsKey(string? actual, string expected) =>
        actual is not null && (string.Equals(actual, expected, StringComparison.Ordinal) ||
            actual.StartsWith(expected + "/cryptoKeyVersions/", StringComparison.Ordinal));

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

    static string PartName(string prefix, int partNumber) => $"{prefix}/part-{partNumber:D5}";

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

    readonly record struct Source(string Name, long Generation);

    internal sealed record SessionPayload(
        string Bucket,
        string KmsKey,
        string ObjectKey,
        string Prefix,
        string Sha256,
        long Length,
        DateTimeOffset ExpiresAt)
    {
        public string Encode(IDataProtector protector) => protector.Protect(
            JsonSerializer.Serialize(this, GcpUploadSessionJsonContext.Default.SessionPayload));

        public static SessionPayload Decode(string token, IDataProtector protector)
        {
            try
            {
                var json = protector.Unprotect(token);
                var payload = JsonSerializer.Deserialize(json, GcpUploadSessionJsonContext.Default.SessionPayload);
                if (payload is null || string.IsNullOrWhiteSpace(payload.Bucket) || string.IsNullOrWhiteSpace(payload.KmsKey) ||
                    string.IsNullOrWhiteSpace(payload.ObjectKey) || string.IsNullOrWhiteSpace(payload.Prefix) ||
                    string.IsNullOrWhiteSpace(payload.Sha256) || payload.Sha256.Length != 64 || !payload.Sha256.All(Uri.IsHexDigit) ||
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

    /// <summary>A forward-only read stream over the pinned generations of the ordered parts.</summary>
    sealed class PartsStream(StorageClient client, string bucket, IReadOnlyList<Source> sources, CancellationToken ct) : Stream
    {
        int _next;
        Stream? _current;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_current is null)
                {
                    if (_next == sources.Count)
                    {
                        return 0;
                    }

                    var source = sources[_next++];
                    var request = client.Service.Objects.Get(bucket, source.Name);
                    request.Generation = source.Generation;
                    request.Alt = Google.Apis.Storage.v1.StorageBaseServiceRequest<GcsObject>.AltEnum.Media;
                    _current = await request.ExecuteAsStreamAsync(cancellationToken.CanBeCanceled ? cancellationToken : ct).ConfigureAwait(false);
                }

                var read = await _current.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0)
                {
                    return read;
                }

                await _current.DisposeAsync().ConfigureAwait(false);
                _current = null;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _current?.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_current is not null)
            {
                await _current.DisposeAsync().ConfigureAwait(false);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}

[JsonSerializable(typeof(GcpObjectStorage.SessionPayload))]
sealed partial class GcpUploadSessionJsonContext : JsonSerializerContext;

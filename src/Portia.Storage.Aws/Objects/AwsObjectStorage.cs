namespace Cntryl.Portia.Storage.Aws;

/// <summary>Implements immutable tenant-scoped objects with Amazon S3.</summary>
public sealed class AwsObjectStorage(IAmazonS3 client, IObjectStorageTenantResolver tenantResolver, IDataProtector sessionProtector) : IObjectStorage
{
    const string DigestMetadata = "portia-sha256";
    const int MaximumParts = 10_000;
    const int MaximumConditionalCopyAttempts = 3;
    static readonly TimeSpan UploadLifetime = TimeSpan.FromHours(24);

    readonly IAmazonS3 _client = client ?? throw new ArgumentNullException(nameof(client));
    readonly IObjectStorageTenantResolver _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
    readonly IDataProtector _sessionProtector = sessionProtector ?? throw new ArgumentNullException(nameof(sessionProtector));

    /// <inheritdoc />
    public async ValueTask<ObjectUploadSession> CreateUploadAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        var key = $"staging/{Guid.NewGuid():N}";
        var response = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = location.BucketReference,
            Key = key,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
            ServerSideEncryptionKeyManagementServiceKeyId = location.EncryptionKeyReference,
            Metadata = { [DigestMetadata] = expected.Sha256, ["portia-length"] = expected.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        }, ct).ConfigureAwait(false);
        return new(new SessionPayload(
            location.BucketReference,
            location.EncryptionKeyReference,
            key,
            response.UploadId,
            expected.Sha256,
            expected.Length,
            DateTimeOffset.UtcNow.Add(UploadLifetime)).Encode(_sessionProtector));
    }

    /// <inheritdoc />
    public ValueTask<ObjectUploadPart> SignPartAsync(ObjectUploadSession session, int partNumber, TimeSpan lifetime, CancellationToken ct = default)
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
        var uri = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = payload.Bucket,
            Key = payload.StagingKey,
            UploadId = payload.UploadId,
            PartNumber = partNumber,
            Verb = HttpVerb.PUT,
            Protocol = PresignedProtocol,
            Expires = expiresAt.UtcDateTime
        });
        return ValueTask.FromResult(new ObjectUploadPart(partNumber, new Uri(uri), expiresAt));
    }

    /// <inheritdoc />
    public async ValueTask CompleteUploadAsync(ObjectUploadSession session, IReadOnlyList<ObjectPartReceipt> parts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(parts);
        var payload = SessionPayload.Decode(session.Token, _sessionProtector);
        EnsureSessionActive(payload);
        // A canceled call can be a retry after S3 committed completion but lost its response.
        // Only an explicit abort can safely discard its potentially completed staging object.
        ct.ThrowIfCancellationRequested();

        if (parts.Count is < 1 or > MaximumParts || parts.Any(static part => part is null || string.IsNullOrWhiteSpace(part.Token)) ||
            parts.Select(static part => part.PartNumber).Distinct().Count() != parts.Count ||
            parts.Any(static part => part.PartNumber is < 1 or > MaximumParts))
        {
            throw new ArgumentException("Multipart completion requires unique, valid part receipts.", nameof(parts));
        }

        var ordered = parts.OrderBy(static part => part.PartNumber).ToArray();
        var uploadIdGone = false;
        try
        {
            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = payload.Bucket,
                Key = payload.StagingKey,
                UploadId = payload.UploadId,
                PartETags = ordered.Select(static part => new PartETag(part.PartNumber, part.Token)).ToList()
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchUpload")
        {
            // The upload ID is also gone after a successful completion. The staged object may
            // still need verification and promotion after a lost response or host restart.
            uploadIdGone = true;
        }

        if (uploadIdGone && await HeadAndVerifyAsync(payload.Bucket, payload.Sha256, payload.Length, ct).ConfigureAwait(false))
        {
            await CleanupStagingAsync(payload, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        (string Digest, string ETag) verified;
        try
        {
            verified = await VerifyStagingAsync(payload, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await CleanupStagingAsync(payload, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // A concurrent completion may have promoted and removed staging while this caller
            // was retrying. An absent destination leaves the outcome unresolved for another retry.
            if (await HeadAndVerifyAsync(payload.Bucket, payload.Sha256, payload.Length, ct).ConfigureAwait(false))
            {
                await CleanupStagingAsync(payload, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            throw;
        }

        await PromoteVerifiedAsync(payload, verified.Digest, verified.ETag, ct).ConfigureAwait(false);
        await CleanupStagingAsync(payload, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AbortUploadAsync(ObjectUploadSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var payload = SessionPayload.Decode(session.Token, _sessionProtector);
        try
        {
            await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = payload.Bucket,
                Key = payload.StagingKey,
                UploadId = payload.UploadId
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound || exception.ErrorCode == "NoSuchUpload")
        {
            // Idempotent abort: the upload may already have completed or been removed.
        }
        finally
        {
            await CleanupStagingAsync(payload, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> HeadAndVerifyAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        return await HeadAndVerifyAsync(location.BucketReference, expected.Sha256, expected.Length, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ObjectDownload> CreateDownloadAsync(TenantId tenantId, ObjectDigest expected, TimeSpan lifetime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var duration = BoundLifetime(lifetime);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        if (!await HeadAndVerifyAsync(location.BucketReference, expected.Sha256, expected.Length, ct).ConfigureAwait(false))
        {
            throw new FileNotFoundException("The verified object was not found.");
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(duration);
        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = location.BucketReference,
            Key = ContentKey(expected.Sha256),
            Verb = HttpVerb.GET,
            Protocol = PresignedProtocol,
            Expires = expiresAt.UtcDateTime
        });
        return new(new Uri(url), expiresAt);
    }

    /// <inheritdoc />
    public async ValueTask<Stream?> OpenReadAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        if (!await HeadAndVerifyAsync(location.BucketReference, expected.Sha256, expected.Length, ct)
            .ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var response = await _client.GetObjectAsync(location.BucketReference, ContentKey(expected.Sha256), ct).ConfigureAwait(false);
            return new ResponseStream(response);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var location = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        await _client.DeleteObjectAsync(location.BucketReference, ContentKey(expected.Sha256), ct).ConfigureAwait(false);
    }

    async ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct)
    {
        var location = await _tenantResolver.ResolveAsync(tenantId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(location.BucketReference) || string.IsNullOrWhiteSpace(location.EncryptionKeyReference))
        {
            throw new InvalidOperationException("Tenant storage resolution must provide bucket and encryption-key references.");
        }

        return location;
    }

    async ValueTask<(string Digest, string ETag)> VerifyStagingAsync(SessionPayload payload, CancellationToken ct)
    {
        using var staged = await _client.GetObjectAsync(payload.Bucket, payload.StagingKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(staged.ETag))
        {
            throw new InvalidOperationException("S3 did not return an ETag for the staged object.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var read = await staged.ResponseStream.ReadAsync(buffer, ct).ConfigureAwait(false);
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

        return (Convert.ToHexString(computed).ToLowerInvariant(), staged.ETag);
    }

    async ValueTask PromoteVerifiedAsync(SessionPayload payload, string verifiedDigest, string stagedETag, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaximumConditionalCopyAttempts; attempt++)
        {
            try
            {
                await _client.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = payload.Bucket,
                    SourceKey = payload.StagingKey,
                    ETagToMatch = stagedETag,
                    DestinationBucket = payload.Bucket,
                    DestinationKey = ContentKey(payload.Sha256),
                    MetadataDirective = S3MetadataDirective.REPLACE,
                    ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
                    ServerSideEncryptionKeyManagementServiceKeyId = payload.KmsKey,
                    Metadata = { [DigestMetadata] = verifiedDigest, ["portia-length"] = payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    IfNoneMatch = "*"
                }, ct).ConfigureAwait(false);
                return;
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict)
            {
                if (await HeadAndVerifyAsync(payload.Bucket, payload.Sha256, payload.Length, ct).ConfigureAwait(false))
                {
                    return;
                }

                if (exception.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
                {
                    throw new InvalidDataException("An object already exists at the immutable content key but failed verification.", exception);
                }

                if (attempt == MaximumConditionalCopyAttempts)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    async ValueTask<bool> HeadAndVerifyAsync(string bucket, string sha256, long length, CancellationToken ct)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(bucket, ContentKey(sha256), ct).ConfigureAwait(false);
            return response.ContentLength == length &&
                response.Metadata[DigestMetadata] is { } digest && string.Equals(digest, sha256, StringComparison.Ordinal);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    async ValueTask CleanupStagingAsync(SessionPayload payload, CancellationToken ct = default)
    {
        try
        {
            await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = payload.Bucket,
                Key = payload.StagingKey,
                UploadId = payload.UploadId
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound || exception.ErrorCode == "NoSuchUpload")
        {
            // The upload may already be completed or aborted.
        }

        try
        {
            await _client.DeleteObjectAsync(payload.Bucket, payload.StagingKey, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Staging cleanup is idempotent.
        }
    }

    static TimeSpan BoundLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > ObjectStorageLimits.MaximumDownloadLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), $"Lifetime must be positive and no longer than {ObjectStorageLimits.MaximumDownloadLifetime}.");
        }

        return lifetime;
    }

    Protocol PresignedProtocol
    {
        get
        {
            var config = _client.Config;
            if (Uri.TryCreate(config.ServiceURL, UriKind.Absolute, out var endpoint))
            {
                return endpoint.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
            }

            return config.UseHttp ? Protocol.HTTP : Protocol.HTTPS;
        }
    }

    static void EnsureSessionActive(SessionPayload payload)
    {
        if (payload.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The upload session has expired.");
        }
    }

    static string ContentKey(string digest) => $"content/sha256/{digest[..2]}/{digest}";

    internal sealed record SessionPayload(
        string Bucket,
        string KmsKey,
        string StagingKey,
        string UploadId,
        string Sha256,
        long Length,
        DateTimeOffset ExpiresAt)
    {
        public string Encode(IDataProtector protector) => protector.Protect(
            JsonSerializer.Serialize(this, AwsUploadSessionJsonContext.Default.SessionPayload));

        public static SessionPayload Decode(string token, IDataProtector protector)
        {
            try
            {
                var json = protector.Unprotect(token);
                var payload = JsonSerializer.Deserialize(json, AwsUploadSessionJsonContext.Default.SessionPayload);
                if (payload is null || string.IsNullOrWhiteSpace(payload.Bucket) || string.IsNullOrWhiteSpace(payload.KmsKey) ||
                    string.IsNullOrWhiteSpace(payload.StagingKey) || string.IsNullOrWhiteSpace(payload.UploadId) ||
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

    sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        readonly GetObjectResponse _response = response;
        readonly Stream _inner = response.ResponseStream;
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
        protected override void Dispose(bool disposing) { if (disposing) _response.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync()
        {
            Dispose();
            return base.DisposeAsync();
        }
    }
}

[JsonSerializable(typeof(AwsObjectStorage.SessionPayload))]
sealed partial class AwsUploadSessionJsonContext : JsonSerializerContext;

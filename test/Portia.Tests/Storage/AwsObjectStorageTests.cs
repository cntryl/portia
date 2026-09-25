namespace Cntryl.Portia.Tests.Storage;

/// <summary>Checks AWS request construction and object verification without cloud credentials.</summary>
public sealed class AwsObjectStorageTests
{
    /// <summary>Checks encrypted staging, verified digest metadata, and conditional immutable promotion.</summary>
    [Fact]
    public async Task ShouldResolveTenantKeyAndPromoteVerifiedContentConditionally()
    {
        var content = "object bytes"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var tenant = new TenantId("tenant-one");
        var resolver = new TestTenantResolver();
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var provider = services.BuildServiceProvider();
        var protector = provider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Cntryl.Portia.Storage.Aws.UploadSession.v1");
        var storage = new AwsObjectStorage(client, resolver, protector);

        var session = await storage.CreateUploadAsync(tenant, digest);
        Assert.DoesNotContain("tenant-one-bucket", session.Token, StringComparison.Ordinal);
        Assert.DoesNotContain("alias/tenant-one-key", session.Token, StringComparison.Ordinal);
        var signed = await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(4));
        Assert.True(signed.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(4));
        Assert.Equal(Protocol.HTTPS, proxy.PresignedRequest?.Protocol);
        await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "opaque-etag")]);

        Assert.Equal("tenant-one-bucket", resolver.LastTenantLocation?.BucketReference);
        Assert.Equal(ServerSideEncryptionMethod.AWSKMS, proxy.InitiateRequest?.ServerSideEncryptionMethod);
        Assert.Equal("alias/tenant-one-key", proxy.InitiateRequest?.ServerSideEncryptionKeyManagementServiceKeyId);
        Assert.StartsWith("staging/", proxy.InitiateRequest?.Key, StringComparison.Ordinal);
        Assert.Equal("content/sha256/" + digest.Sha256[..2] + "/" + digest.Sha256, proxy.CopyRequest?.DestinationKey);
        Assert.Equal("\"staged-etag\"", proxy.CopyRequest?.ETagToMatch);
        Assert.Equal("*", proxy.CopyRequest?.IfNoneMatch);
        Assert.Equal(ServerSideEncryptionMethod.AWSKMS, proxy.CopyRequest?.ServerSideEncryptionMethod);
        Assert.Equal("alias/tenant-one-key", proxy.CopyRequest?.ServerSideEncryptionKeyManagementServiceKeyId);
        Assert.Equal(digest.Sha256, proxy.CopyRequest?.Metadata["portia-sha256"]);
        Assert.Equal(digest.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), proxy.CopyRequest?.Metadata["portia-length"]);
        Assert.Equal(1, proxy.AbortCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
    }

    /// <summary>Uses the configured S3 endpoint scheme for signed part and download links.</summary>
    [Fact]
    public async Task ShouldSignHttpUrlsForHttpS3Endpoints()
    {
        var content = "local endpoint"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        proxy.ServiceUrl = "http://127.0.0.1:9000";
        proxy.ExistingObject = true;
        var storage = CreateStorage(client);
        var tenant = new TenantId("tenant-one");
        var session = await storage.CreateUploadAsync(tenant, digest);

        await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1));
        Assert.Equal(Protocol.HTTP, proxy.PresignedRequest?.Protocol);

        await storage.CreateDownloadAsync(tenant, digest, TimeSpan.FromMinutes(1));
        Assert.Equal(Protocol.HTTP, proxy.PresignedRequest?.Protocol);
    }

    /// <summary>Uses the S3 client's HTTP setting when it has no explicit service URL.</summary>
    [Fact]
    public async Task ShouldSignHttpUrlsWhenS3ClientUsesHttp()
    {
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.UseHttp = true;
        var storage = CreateStorage(client);
        var digest = new ObjectDigest(new string('a', 64), 1);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);

        await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1));

        Assert.Equal(Protocol.HTTP, proxy.PresignedRequest?.Protocol);
    }

    /// <summary>Checks that a staged full-object digest mismatch never reaches the content key.</summary>
    [Fact]
    public async Task ShouldRejectMismatchedStagedBytesAndCleanUpTheMultipartUpload()
    {
        var content = "correct"u8.ToArray();
        var wrongContent = "changed"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = wrongContent;
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var provider = services.BuildServiceProvider();
        var storage = new AwsObjectStorage(
            client,
            new TestTenantResolver(),
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Cntryl.Portia.Storage.Aws.UploadSession.v1"));
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "etag")]));

        Assert.Null(proxy.CopyRequest);
        Assert.Equal(1, proxy.AbortCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
    }

    /// <summary>Checks that a lost completion response can be retried after immutable promotion.</summary>
    [Fact]
    public async Task ShouldTreatVerifiedPromotionAsSuccessfulWhenCompletionIsRetried()
    {
        var content = "retryable object"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        proxy.FailRepeatedCompletion = true;
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var provider = services.BuildServiceProvider();
        var storage = new AwsObjectStorage(
            client,
            new TestTenantResolver(),
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Cntryl.Portia.Storage.Aws.UploadSession.v1"));
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var parts = new[] { new ObjectPartReceipt(1, "etag") };

        await storage.CompleteUploadAsync(session, parts);
        await storage.CompleteUploadAsync(session, parts);

        Assert.Equal(2, proxy.CompleteCount);
        Assert.Equal(1, proxy.CopyCount);
        Assert.Equal(2, proxy.AbortCount);
        Assert.Equal(2, proxy.StagingDeleteCount);
    }

    /// <summary>Checks that a concurrent completion cannot lose its staged input during retry.</summary>
    [Fact]
    public async Task ShouldKeepStagingWhenUploadIdIsGoneButPromotionIsNotVisibleYet()
    {
        var content = "concurrent completion"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        proxy.FailAllCompletions = true;
        proxy.StagingObjectMissing = true;
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var provider = services.BuildServiceProvider();
        var storage = new AwsObjectStorage(
            client,
            new TestTenantResolver(),
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Cntryl.Portia.Storage.Aws.UploadSession.v1"));
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);

        await Assert.ThrowsAsync<AmazonS3Exception>(async () =>
            await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "etag")]));

        Assert.Equal(0, proxy.AbortCount);
        Assert.Equal(0, proxy.StagingDeleteCount);
    }

    /// <summary>Checks that a completed staging object can be verified and promoted on retry.</summary>
    [Fact]
    public async Task ShouldResumePromotionWhenTheMultipartUploadIdIsGone()
    {
        var content = "completed staging object"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        proxy.FailAllCompletions = true;
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);

        await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "etag")]);

        Assert.Equal(1, proxy.CompleteCount);
        Assert.Equal(1, proxy.CopyCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
        Assert.Equal(digest.Sha256, proxy.CopyRequest?.Metadata["portia-sha256"]);
    }

    /// <summary>Checks that a lost completion response leaves the staged bytes available for retry.</summary>
    [Fact]
    public async Task ShouldPreserveAndResumeAfterAmbiguousMultipartCompletion()
    {
        var content = "ambiguous completion"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        proxy.TimeoutAfterFirstCompletion = true;
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var parts = new[] { new ObjectPartReceipt(1, "etag") };

        await Assert.ThrowsAsync<TimeoutException>(async () => await storage.CompleteUploadAsync(session, parts));
        Assert.Equal(0, proxy.AbortCount);
        Assert.Equal(0, proxy.StagingDeleteCount);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await storage.CompleteUploadAsync(session, parts, canceled.Token));
        Assert.Equal(0, proxy.AbortCount);
        Assert.Equal(0, proxy.StagingDeleteCount);

        await storage.CompleteUploadAsync(session, parts);
        Assert.Equal(2, proxy.CompleteCount);
        Assert.Equal(1, proxy.CopyCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
    }

    /// <summary>Checks that cancellation after S3 receives completion leaves a retry path.</summary>
    [Fact]
    public async Task ShouldPreserveStagingWhenCompletionIsCanceledInFlight()
    {
        var content = "in-flight cancellation"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        using var source = new CancellationTokenSource();
        proxy.CancelDuringFirstCompletion = source;
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var parts = new[] { new ObjectPartReceipt(1, "etag") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await storage.CompleteUploadAsync(session, parts, source.Token));
        Assert.Equal(0, proxy.AbortCount);
        Assert.Equal(0, proxy.StagingDeleteCount);

        await storage.CompleteUploadAsync(session, parts);
        Assert.Equal(1, proxy.CopyCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
    }

    /// <summary>Checks that a bad completed staging object is discarded during recovery.</summary>
    [Fact]
    public async Task ShouldRejectAndCleanMismatchedStagingOnResume()
    {
        var content = "expected content"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = "corrupt content"u8.ToArray();
        proxy.FailAllCompletions = true;
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "etag")]));

        Assert.Equal(0, proxy.CopyCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
    }

    /// <summary>Checks that a transient destination conflict retries without discarding verified bytes.</summary>
    [Fact]
    public async Task ShouldRetryConditionalCopyConflict()
    {
        var content = "conflicting promotion"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        proxy.ConditionalCopyConflictsRemaining = 1;
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);

        await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "etag")]);

        Assert.Equal(2, proxy.CopyCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
    }

    /// <summary>Checks that unresolved conditional conflicts retain staged bytes for a later retry.</summary>
    [Fact]
    public async Task ShouldKeepStagingAfterRepeatedConditionalCopyConflicts()
    {
        var content = "persistent conflict"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        proxy.ConditionalCopyConflictsRemaining = 10;
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);

        await Assert.ThrowsAsync<AmazonS3Exception>(async () =>
            await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "etag")]));

        Assert.InRange(proxy.CopyCount, 1, 3);
        Assert.Equal(0, proxy.StagingDeleteCount);
    }

    /// <summary>Checks that an immutable-copy race succeeds only when the existing object verifies.</summary>
    [Fact]
    public async Task ShouldAcceptVerifiedConditionalCreateRace()
    {
        var content = "concurrent immutable object"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        proxy.FailCopyConditionally = true;
        proxy.ExistingObject = true;
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);

        await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "etag")]);

        Assert.Equal(1, proxy.CopyCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
    }

    /// <summary>Checks that missing and incorrectly described promoted objects are not reported as valid.</summary>
    [Fact]
    public async Task ShouldReportMissingOrUnverifiedContentAsUnavailable()
    {
        var content = "existing object"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        var storage = CreateStorage(client);
        var tenant = new TenantId("tenant-one");

        proxy.MissingObject = true;
        Assert.False(await storage.HeadAndVerifyAsync(tenant, digest));
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await storage.CreateDownloadAsync(tenant, digest, TimeSpan.FromMinutes(1)));

        proxy.MissingObject = false;
        proxy.ExistingObject = true;
        proxy.MetadataDigest = new string('0', 64);
        Assert.False(await storage.HeadAndVerifyAsync(tenant, digest));
    }

    /// <summary>A delete between metadata verification and reading has the same missing-object result.</summary>
    [Fact]
    public async Task ShouldReturnNoStreamWhenContentDisappearsAfterHead()
    {
        var content = "concurrent deletion"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        proxy.ExistingObject = true;
        proxy.MissingPromotedObjectOnGet = true;
        var storage = CreateStorage(client);

        Assert.Null(await storage.OpenReadAsync(new TenantId("tenant-one"), digest));
    }

    /// <summary>Checks cancellation leaves a session recoverable until the caller explicitly aborts it.</summary>
    [Fact]
    public async Task ShouldPreserveStagingWhenCompletionIsCanceledBeforeS3Request()
    {
        var content = "cancelled object"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.Content = content;
        proxy.CancelCompletion = true;
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "etag")], source.Token));

        Assert.Equal(0, proxy.AbortCount);
        Assert.Equal(0, proxy.StagingDeleteCount);
        Assert.Equal(0, proxy.CopyCount);

        await storage.AbortUploadAsync(session);
        Assert.Equal(2, proxy.AbortCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
    }

    /// <summary>Checks that abort remains idempotent after S3 has removed the upload ID.</summary>
    [Fact]
    public async Task ShouldAbortWhenS3ReportsNoSuchUpload()
    {
        var content = "aborted object"u8.ToArray();
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var client = DispatchProxy.Create<IAmazonS3, TestS3ClientProxy>();
        var proxy = (TestS3ClientProxy)(object)client;
        proxy.AbortReportsNoSuchUpload = true;
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);

        await storage.AbortUploadAsync(session);

        Assert.Equal(2, proxy.AbortCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
    }

    static AwsObjectStorage CreateStorage(IAmazonS3 client)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var provider = services.BuildServiceProvider();
        return new AwsObjectStorage(
            client,
            new TestTenantResolver(),
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Cntryl.Portia.Storage.Aws.UploadSession.v1"));
    }

    /// <summary>A tenant-to-bucket and KMS mapping used by the request-boundary tests.</summary>
    public sealed class TestTenantResolver : IObjectStorageTenantResolver
    {
        /// <summary>Gets the location returned for the most recent tenant resolution.</summary>
        public ObjectStorageLocation? LastTenantLocation { get; private set; }

        /// <inheritdoc />
        public ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var location = new ObjectStorageLocation($"{tenantId.Value}-bucket", $"alias/{tenantId.Value}-key");
            LastTenantLocation = location;
            return ValueTask.FromResult(location);
        }
    }

    /// <summary>A focused IAmazonS3 proxy that captures writes and supplies staged bytes.</summary>
    public class TestS3ClientProxy : DispatchProxy
    {
        /// <summary>Gets or sets the bytes returned when the adapter reads the staged object.</summary>
        public byte[] Content { get; set; } = [];

        /// <summary>Gets or sets the S3 endpoint exposed by the configured client.</summary>
        public string? ServiceUrl { get; set; }

        /// <summary>Gets or sets whether the S3 client uses HTTP without an explicit service URL.</summary>
        public bool UseHttp { get; set; }

        /// <summary>Gets the most recent signed URL request.</summary>
        public GetPreSignedUrlRequest? PresignedRequest { get; private set; }

        /// <summary>Gets the multipart creation request.</summary>
        public InitiateMultipartUploadRequest? InitiateRequest { get; private set; }

        /// <summary>Gets the conditional promotion request.</summary>
        public CopyObjectRequest? CopyRequest { get; private set; }

        /// <summary>Gets the number of multipart abort calls.</summary>
        public int AbortCount { get; private set; }

        /// <summary>Gets the number of staging object deletes.</summary>
        public int StagingDeleteCount { get; private set; }

        /// <summary>Gets or sets whether a repeated completion reports that its upload ID is gone.</summary>
        public bool FailRepeatedCompletion { get; set; }

        /// <summary>Gets or sets whether every completion reports that its upload ID is gone.</summary>
        public bool FailAllCompletions { get; set; }

        /// <summary>Gets or sets whether the first completion loses its response after S3 commits it.</summary>
        public bool TimeoutAfterFirstCompletion { get; set; }

        /// <summary>Gets or sets the cancellation source to signal after completion starts.</summary>
        public CancellationTokenSource? CancelDuringFirstCompletion { get; set; }

        /// <summary>Gets or sets whether the staging object is unavailable to a retry.</summary>
        public bool StagingObjectMissing { get; set; }

        /// <summary>Gets or sets the number of conditional-copy 409 responses to simulate.</summary>
        public int ConditionalCopyConflictsRemaining { get; set; }

        /// <summary>Gets or sets whether immutable promotion loses a precondition race.</summary>
        public bool FailCopyConditionally { get; set; }

        /// <summary>Gets or sets whether a metadata check reports the content key as missing.</summary>
        public bool MissingObject { get; set; }

        /// <summary>Gets or sets whether a promoted object disappears after a successful metadata check.</summary>
        public bool MissingPromotedObjectOnGet { get; set; }

        /// <summary>Gets or sets whether a content object should be visible to metadata checks.</summary>
        public bool ExistingObject { get; set; }

        /// <summary>Gets or sets a digest to return instead of the digest of the staged bytes.</summary>
        public string? MetadataDigest { get; set; }

        /// <summary>Gets or sets whether completion throws cancellation before reaching S3.</summary>
        public bool CancelCompletion { get; set; }

        /// <summary>Gets or sets whether abort reports that the multipart upload is already gone.</summary>
        public bool AbortReportsNoSuchUpload { get; set; }

        /// <summary>Gets the number of multipart completion calls.</summary>
        public int CompleteCount { get; private set; }

        /// <summary>Gets the number of immutable-copy calls.</summary>
        public int CopyCount { get; private set; }

        bool PromotedObjectExists { get; set; }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            var responseType = targetMethod.ReturnType.GenericTypeArguments.FirstOrDefault();
            var response = targetMethod.Name switch
            {
                nameof(IAmazonS3.InitiateMultipartUploadAsync) => Box(Initiate(args)),
                nameof(IAmazonS3.CompleteMultipartUploadAsync) => Box(Complete(args)),
                nameof(IAmazonS3.GetObjectAsync) => Box(GetObjectResponseForCall(args)),
                nameof(IAmazonS3.CopyObjectAsync) => Box(CaptureCopy(args)),
                nameof(IAmazonS3.AbortMultipartUploadAsync) => Box(CountAbort()),
                nameof(IAmazonS3.DeleteObjectAsync) => Box(CountDelete(args)),
                nameof(IAmazonS3.GetPreSignedURL) => Box(CaptureSignedUrl(args)),
                nameof(IAmazonS3.GetObjectMetadataAsync) => Box(GetMetadata()),
                "get_Config" => Box(new AmazonS3Config { ServiceURL = ServiceUrl, UseHttp = UseHttp }),
                _ => throw new NotSupportedException($"Unexpected AWS S3 operation: {targetMethod.Name}.")
            };

            return responseType is null
                ? response
                : typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(responseType).Invoke(null, [response]);
        }

        static object Box<T>(T value) where T : notnull => value;

        string CaptureSignedUrl(object?[] args)
        {
            PresignedRequest = (GetPreSignedUrlRequest)args[0]!;
            return "https://objects.example.invalid/signed";
        }

        InitiateMultipartUploadResponse Initiate(object?[] args)
        {
            InitiateRequest = (InitiateMultipartUploadRequest)args[0]!;
            return new InitiateMultipartUploadResponse { UploadId = "test-upload-id" };
        }

        GetObjectResponse GetObjectResponseForCall(object?[] args)
        {
            var key = args[0] is GetObjectRequest request ? request.Key : args.ElementAtOrDefault(1) as string;
            if (MissingPromotedObjectOnGet && key?.StartsWith("content/", StringComparison.Ordinal) == true)
            {
                throw new AmazonS3Exception("The promoted object was deleted.") { StatusCode = System.Net.HttpStatusCode.NotFound };
            }

            if (StagingObjectMissing)
            {
                throw new AmazonS3Exception("The staging object does not exist.") { StatusCode = System.Net.HttpStatusCode.NotFound };
            }

            return new GetObjectResponse { ETag = "\"staged-etag\"", ResponseStream = new MemoryStream(Content, writable: false) };
        }

        CopyObjectResponse CaptureCopy(object?[] args)
        {
            CopyCount++;
            CopyRequest = (CopyObjectRequest)args[0]!;
            if (FailCopyConditionally)
            {
                throw new AmazonS3Exception("The destination object already exists.")
                {
                    StatusCode = System.Net.HttpStatusCode.PreconditionFailed
                };
            }

            if (ConditionalCopyConflictsRemaining > 0)
            {
                ConditionalCopyConflictsRemaining--;
                throw new AmazonS3Exception("A conflicting conditional operation is in progress.")
                {
                    StatusCode = System.Net.HttpStatusCode.Conflict,
                    ErrorCode = "ConditionalRequestConflict"
                };
            }

            PromotedObjectExists = true;
            return new CopyObjectResponse();
        }

        CompleteMultipartUploadResponse Complete(object?[] args)
        {
            CompleteCount++;
            if (CancelCompletion && args.LastOrDefault() is CancellationToken { IsCancellationRequested: true } token)
            {
                throw new OperationCanceledException(token);
            }

            if (TimeoutAfterFirstCompletion && CompleteCount == 1)
            {
                throw new TimeoutException("The response was lost after S3 completed the upload.");
            }

            if (CancelDuringFirstCompletion is { } source && CompleteCount == 1)
            {
                source.Cancel();
                throw new OperationCanceledException(source.Token);
            }

            if (FailAllCompletions || (FailRepeatedCompletion && CompleteCount > 1) ||
                (TimeoutAfterFirstCompletion && CompleteCount > 1) ||
                (CancelDuringFirstCompletion is not null && CompleteCount > 1))
            {
                throw new AmazonS3Exception("The upload ID no longer exists.") { ErrorCode = "NoSuchUpload" };
            }

            return new CompleteMultipartUploadResponse();
        }

        GetObjectMetadataResponse GetMetadata()
        {
            if (MissingObject)
            {
                throw new AmazonS3Exception("The object does not exist.") { StatusCode = System.Net.HttpStatusCode.NotFound };
            }

            var response = new GetObjectMetadataResponse { ContentLength = Content.LongLength };
            if (PromotedObjectExists || ExistingObject)
            {
                response.Metadata["portia-sha256"] = MetadataDigest ?? Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();
            }

            return response;
        }

        AbortMultipartUploadResponse CountAbort()
        {
            AbortCount++;
            if (AbortReportsNoSuchUpload)
            {
                throw new AmazonS3Exception("The upload ID no longer exists.") { ErrorCode = "NoSuchUpload" };
            }

            return new AbortMultipartUploadResponse();
        }

        DeleteObjectResponse CountDelete(object?[] args)
        {
            if (args[0] is DeleteObjectRequest request && request.Key.StartsWith("staging/", StringComparison.Ordinal))
            {
                StagingDeleteCount++;
                StagingObjectMissing = true;
            }
            else if (args[0] is string && args.Length > 1 && ((string)args[1]!).StartsWith("staging/", StringComparison.Ordinal))
            {
                StagingDeleteCount++;
                StagingObjectMissing = true;
            }

            return new DeleteObjectResponse();
        }
    }
}

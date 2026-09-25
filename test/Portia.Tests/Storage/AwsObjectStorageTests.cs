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
        await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "opaque-etag")]);

        Assert.Equal("tenant-one-bucket", resolver.LastTenantLocation?.BucketReference);
        Assert.Equal(ServerSideEncryptionMethod.AWSKMS, proxy.InitiateRequest?.ServerSideEncryptionMethod);
        Assert.Equal("alias/tenant-one-key", proxy.InitiateRequest?.ServerSideEncryptionKeyManagementServiceKeyId);
        Assert.StartsWith("staging/", proxy.InitiateRequest?.Key, StringComparison.Ordinal);
        Assert.Equal("content/sha256/" + digest.Sha256[..2] + "/" + digest.Sha256, proxy.CopyRequest?.DestinationKey);
        Assert.Equal("*", proxy.CopyRequest?.IfNoneMatch);
        Assert.Equal(ServerSideEncryptionMethod.AWSKMS, proxy.CopyRequest?.ServerSideEncryptionMethod);
        Assert.Equal("alias/tenant-one-key", proxy.CopyRequest?.ServerSideEncryptionKeyManagementServiceKeyId);
        Assert.Equal(digest.Sha256, proxy.CopyRequest?.Metadata["portia-sha256"]);
        Assert.Equal(digest.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), proxy.CopyRequest?.Metadata["portia-length"]);
        Assert.Equal(1, proxy.AbortCount);
        Assert.Equal(1, proxy.StagingDeleteCount);
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

        /// <summary>Gets the number of multipart completion calls.</summary>
        public int CompleteCount { get; private set; }

        /// <summary>Gets the number of immutable-copy calls.</summary>
        public int CopyCount { get; private set; }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            var responseType = targetMethod.ReturnType.GenericTypeArguments.FirstOrDefault();
            var response = targetMethod.Name switch
            {
                nameof(IAmazonS3.InitiateMultipartUploadAsync) => Box(Initiate(args)),
                nameof(IAmazonS3.CompleteMultipartUploadAsync) => Box(Complete()),
                nameof(IAmazonS3.GetObjectAsync) => Box(GetObjectResponseForCall()),
                nameof(IAmazonS3.CopyObjectAsync) => Box(CaptureCopy(args)),
                nameof(IAmazonS3.AbortMultipartUploadAsync) => Box(CountAbort()),
                nameof(IAmazonS3.DeleteObjectAsync) => Box(CountDelete(args)),
                nameof(IAmazonS3.GetPreSignedURL) => Box("https://objects.example.invalid/signed"),
                nameof(IAmazonS3.GetObjectMetadataAsync) => Box(GetMetadata()),
                _ => throw new NotSupportedException($"Unexpected AWS S3 operation: {targetMethod.Name}.")
            };

            return responseType is null
                ? response
                : typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(responseType).Invoke(null, [response]);
        }

        static object Box<T>(T value) where T : notnull => value;

        InitiateMultipartUploadResponse Initiate(object?[] args)
        {
            InitiateRequest = (InitiateMultipartUploadRequest)args[0]!;
            return new InitiateMultipartUploadResponse { UploadId = "test-upload-id" };
        }

        GetObjectResponse GetObjectResponseForCall()
            => new() { ResponseStream = new MemoryStream(Content, writable: false) };

        CopyObjectResponse CaptureCopy(object?[] args)
        {
            CopyCount++;
            CopyRequest = (CopyObjectRequest)args[0]!;
            return new CopyObjectResponse();
        }

        CompleteMultipartUploadResponse Complete()
        {
            CompleteCount++;
            if (FailAllCompletions || (FailRepeatedCompletion && CompleteCount > 1))
            {
                throw new AmazonS3Exception("The upload ID no longer exists.") { ErrorCode = "NoSuchUpload" };
            }

            return new CompleteMultipartUploadResponse();
        }

        GetObjectMetadataResponse GetMetadata()
        {
            var response = new GetObjectMetadataResponse { ContentLength = Content.LongLength };
            if (CopyCount > 0)
            {
                response.Metadata["portia-sha256"] = Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();
            }

            return response;
        }

        AbortMultipartUploadResponse CountAbort()
        {
            AbortCount++;
            return new AbortMultipartUploadResponse();
        }

        DeleteObjectResponse CountDelete(object?[] args)
        {
            if (args[0] is DeleteObjectRequest request && request.Key.StartsWith("staging/", StringComparison.Ordinal))
            {
                StagingDeleteCount++;
            }
            else if (args[0] is string && args.Length > 1 && ((string)args[1]!).StartsWith("staging/", StringComparison.Ordinal))
            {
                StagingDeleteCount++;
            }

            return new DeleteObjectResponse();
        }
    }
}

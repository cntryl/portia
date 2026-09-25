using System.Net;
using Amazon.Runtime;

namespace Cntryl.Portia.Tests.Storage;

/// <summary>Checks the public storage flow through the real AWS SDK and Sqrzl's S3 HTTP surface.</summary>
[Trait("Category", "StorageIntegration")]
public sealed class AwsObjectStorageIntegrationTests
{
    /// <summary>Runs the reusable storage contract checks against the S3 adapter.</summary>
    [Fact]
    public async Task ShouldPassSharedConformanceAgainstSqrzl()
    {
        var endpoint = Environment.GetEnvironmentVariable("PORTIA_S3_TEST_ENDPOINT")
            ?? throw new InvalidOperationException("Set PORTIA_S3_TEST_ENDPOINT after starting 'docker compose up -d sqrzl'.");
        var bucketSuffix = Guid.NewGuid().ToString("N");
        var firstBucket = $"portia-conformance-a-{bucketSuffix}";
        var secondBucket = $"portia-conformance-b-{bucketSuffix}";
        var firstTenant = new TenantId("conformance-a");
        var secondTenant = new TenantId("conformance-b");
        var locations = new Dictionary<string, ObjectStorageLocation>(StringComparer.Ordinal)
        {
            [firstTenant.Value] = new(firstBucket, "alias/portia-conformance-a"),
            [secondTenant.Value] = new(secondBucket, "alias/portia-conformance-b")
        };
        using var client = CreateClient(endpoint);
        using var host = BuildHost(client, locations);
        using var transfer = new HttpClient();
        var storage = host.GetRequiredService<IObjectStorage>();

        await client.PutBucketAsync(new PutBucketRequest { BucketName = firstBucket });
        await client.PutBucketAsync(new PutBucketRequest { BucketName = secondBucket });
        try
        {
            await ObjectStorageConformance.VerifyAsync(storage, firstTenant, secondTenant,
                async (part, bytes, ct) =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Put, part.UploadUri)
                    {
                        Content = new ByteArrayContent(bytes.ToArray())
                    };
                    using var response = await transfer.SendAsync(request, ct);
                    response.EnsureSuccessStatusCode();
                    var etag = response.Headers.ETag?.Tag
                        ?? throw new InvalidDataException("The signed part upload returned no ETag.");
                    return new ObjectPartReceipt(part.PartNumber, etag);
                });
        }
        finally
        {
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = firstBucket });
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = secondBucket });
        }
    }

    /// <summary>Checks Sqrzl's conditional PUT and COPY behavior through the configured SDK client.</summary>
    [Fact]
    public async Task ShouldRejectConditionalOverwriteAgainstSqrzl()
    {
        var endpoint = Environment.GetEnvironmentVariable("PORTIA_S3_TEST_ENDPOINT")
            ?? throw new InvalidOperationException("Set PORTIA_S3_TEST_ENDPOINT after starting 'docker compose up -d sqrzl'.");
        var bucket = $"portia-conditional-{Guid.NewGuid():N}";
        using var client = CreateClient(endpoint);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            // Sqrzl does not decode the SDK's default aws-chunked PUT body framing.
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = "source",
                ContentBody = "first",
                UseChunkEncoding = false,
                IfNoneMatch = "*"
            });
            var putConflict = await Assert.ThrowsAsync<AmazonS3Exception>(() => client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = "source",
                ContentBody = "second",
                UseChunkEncoding = false,
                IfNoneMatch = "*"
            }));
            Assert.Equal(HttpStatusCode.PreconditionFailed, putConflict.StatusCode);

            await client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket,
                SourceKey = "source",
                DestinationBucket = bucket,
                DestinationKey = "destination",
                IfNoneMatch = "*"
            });
            var copyConflict = await Assert.ThrowsAsync<AmazonS3Exception>(() => client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket,
                SourceKey = "source",
                DestinationBucket = bucket,
                DestinationKey = "destination",
                IfNoneMatch = "*"
            }));
            Assert.Equal(HttpStatusCode.PreconditionFailed, copyConflict.StatusCode);

            using var source = await client.GetObjectAsync(bucket, "source");
            using var reader = new StreamReader(source.ResponseStream);
            Assert.Equal("first", await reader.ReadToEndAsync());
        }
        finally
        {
            await client.DeleteObjectAsync(bucket, "source");
            await client.DeleteObjectAsync(bucket, "destination");
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
        }
    }

    /// <summary>Proves signed multipart upload, conditional promotion, tenant buckets, reads, and deletion across hosts.</summary>
    [Fact]
    public async Task ShouldCompleteInApiAndReadInSeparateWorkerAgainstSqrzl()
    {
        var endpoint = Environment.GetEnvironmentVariable("PORTIA_S3_TEST_ENDPOINT")
            ?? throw new InvalidOperationException("Set PORTIA_S3_TEST_ENDPOINT after starting 'docker compose up -d sqrzl'.");
        var bucketSuffix = Guid.NewGuid().ToString("N");
        var firstBucket = $"portia-storage-a-{bucketSuffix}";
        var secondBucket = $"portia-storage-b-{bucketSuffix}";
        var firstTenant = new TenantId("tenant-a");
        var secondTenant = new TenantId("tenant-b");
        var locations = new Dictionary<string, ObjectStorageLocation>(StringComparer.Ordinal)
        {
            [firstTenant.Value] = new(firstBucket, "alias/portia-tenant-a"),
            [secondTenant.Value] = new(secondBucket, "alias/portia-tenant-b")
        };
        using var apiClient = CreateClient(endpoint);
        using var workerClient = CreateClient(endpoint);
        using var apiHost = BuildHost(apiClient, locations);
        using var workerHost = BuildHost(workerClient, locations);
        using var transfer = new HttpClient();
        var api = apiHost.GetRequiredService<IObjectStorage>();
        var worker = workerHost.GetRequiredService<IObjectStorage>();
        Assert.NotSame(api, worker);

        await apiClient.PutBucketAsync(new PutBucketRequest { BucketName = firstBucket });
        await apiClient.PutBucketAsync(new PutBucketRequest { BucketName = secondBucket });
        var content = new byte[ObjectStorageLimits.MinimumNonfinalPartLength + 19];
        RandomNumberGenerator.Fill(content);
        var digest = new ObjectDigest(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);
        var key = $"content/sha256/{digest.Sha256[..2]}/{digest.Sha256}";

        try
        {
            await UploadAsync(api, firstTenant, digest, content, transfer);
            Assert.True(await worker.HeadAndVerifyAsync(firstTenant, digest));
            Assert.False(await worker.HeadAndVerifyAsync(secondTenant, digest));
            await using (var read = await worker.OpenReadAsync(firstTenant, digest))
            {
                Assert.NotNull(read);
                using var buffer = new MemoryStream();
                await read.CopyToAsync(buffer);
                Assert.Equal(content, buffer.ToArray());
            }

            var download = await worker.CreateDownloadAsync(firstTenant, digest, TimeSpan.FromMinutes(1));
            Assert.True(download.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.Equal(content, await transfer.GetByteArrayAsync(download.DownloadUri));

            var promoted = await workerClient.GetObjectMetadataAsync(firstBucket, key);
            Assert.Equal(ServerSideEncryptionMethod.AWSKMS, promoted.ServerSideEncryptionMethod);
            Assert.Equal(locations[firstTenant.Value].EncryptionKeyReference,
                promoted.ServerSideEncryptionKeyManagementServiceKeyId);
            Assert.Equal(digest.Sha256, promoted.Metadata["portia-sha256"]);

            // A second upload with the same content key exercises the immutable copy precondition.
            await UploadAsync(api, firstTenant, digest, content, transfer);
            await UploadAsync(api, secondTenant, digest, content, transfer);
            Assert.True(await worker.HeadAndVerifyAsync(secondTenant, digest));
            var secondPromoted = await workerClient.GetObjectMetadataAsync(secondBucket, key);
            Assert.Equal(locations[secondTenant.Value].EncryptionKeyReference,
                secondPromoted.ServerSideEncryptionKeyManagementServiceKeyId);

            await worker.DeleteAsync(firstTenant, digest);
            Assert.False(await worker.HeadAndVerifyAsync(firstTenant, digest));
            Assert.True(await worker.HeadAndVerifyAsync(secondTenant, digest));
            await worker.DeleteAsync(secondTenant, digest);
            Assert.False(await worker.HeadAndVerifyAsync(secondTenant, digest));
        }
        finally
        {
            await apiClient.DeleteObjectAsync(firstBucket, key);
            await apiClient.DeleteObjectAsync(secondBucket, key);
            await apiClient.DeleteBucketAsync(new DeleteBucketRequest { BucketName = firstBucket });
            await apiClient.DeleteBucketAsync(new DeleteBucketRequest { BucketName = secondBucket });
        }
    }

    static AmazonS3Client CreateClient(string endpoint)
    {
        var client = new AmazonS3Client(
            new BasicAWSCredentials("portia-test", "portia-local-secret"),
            new AmazonS3Config
            {
                ServiceURL = endpoint,
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
                UseHttp = true
            });
        client.BeforeRequestEvent += (_, args) =>
        {
            // Sqrzl splits CopySource on literal slashes before URL decoding. The AWS SDK
            // encodes them; normalize only this test request before SigV4 signs it.
            if (args is WebServiceRequestEventArgs { Request: CopyObjectRequest } request &&
                request.Headers.TryGetValue("x-amz-copy-source", out var source))
            {
                request.Headers["x-amz-copy-source"] = source.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
            }
        };
        return client;
    }

    static ServiceProvider BuildHost(IAmazonS3 client, IReadOnlyDictionary<string, ObjectStorageLocation> locations)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<IObjectStorageTenantResolver>(new TestTenantResolver(locations));
        services.AddAwsObjectStorage(options => options.UseClient(client));
        return services.BuildServiceProvider();
    }

    static async Task UploadAsync(IObjectStorage storage, TenantId tenant, ObjectDigest digest, byte[] content, HttpClient transfer)
    {
        var session = await storage.CreateUploadAsync(tenant, digest);
        var parts = new List<ObjectPartReceipt>();
        try
        {
            var partLength = ObjectStorageLimits.MinimumNonfinalPartLength;
            for (var offset = 0; offset < content.Length; offset += partLength)
            {
                var number = parts.Count + 1;
                var length = Math.Min(partLength, content.Length - offset);
                var signed = await storage.SignPartAsync(session, number, TimeSpan.FromMinutes(2));
                using var request = new HttpRequestMessage(HttpMethod.Put, signed.UploadUri)
                {
                    Content = new ByteArrayContent(content, offset, length)
                };
                using var response = await transfer.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var etag = response.Headers.ETag?.Tag
                    ?? throw new InvalidDataException("The signed part upload returned no ETag.");
                parts.Add(new ObjectPartReceipt(number, etag));
            }

        }
        catch
        {
            await storage.AbortUploadAsync(session);
            throw;
        }

        await storage.CompleteUploadAsync(session, parts);
    }

    sealed class TestTenantResolver(IReadOnlyDictionary<string, ObjectStorageLocation> locations) : IObjectStorageTenantResolver
    {
        public ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(locations[tenantId.Value]);
        }
    }
}

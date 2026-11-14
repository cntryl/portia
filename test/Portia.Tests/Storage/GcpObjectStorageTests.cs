using System.Globalization;
using System.Net;
using System.Web;
using Cntryl.Portia.Storage.Gcp;
using Google.Apis.Services;
using Google.Apis.Storage.v1;
using Google.Cloud.Storage.V1;

namespace Cntryl.Portia.Tests.Storage;

/// <summary>Checks Cloud Storage request construction and object verification without cloud credentials.</summary>
public sealed class GcpObjectStorageTests
{
    /// <summary>Completion uses the bucket and keys captured before the application's resolver changes.</summary>
    [Fact]
    public async Task ShouldPinApplicationLayoutGivenResolverChangesDuringUpload()
    {
        // Arrange
        var content = "application owned keys"u8.ToArray();
        var digest = Digest(content);
        var tenant = new TenantId("tenant-one");
        var resolver = new ApplicationStorageResolver(Harness.KeyOf("shared"));
        var expectedKey = resolver.GetObjectKey(tenant, digest);
        var harness = Harness.Create();
        harness.Gcs.BucketKeys[ApplicationStorageResolver.Bucket] = Harness.KeyOf("shared");
        var storage = new GcpObjectStorage(Harness.CreateClient(harness.Gcs), Harness.CreateSigner(), resolver,
            harness.Protection.CreateProtector("layout"));
        var session = await storage.CreateUploadAsync(tenant, digest);
        var part = await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1));
        var receipt = new ObjectPartReceipt(1, harness.Gcs.PutSigned(part.UploadUri, content));
        resolver.ContentRoot = "moved";
        resolver.UploadRoot = "new-uploads";

        // Act
        await storage.CompleteUploadAsync(session, [receipt]);

        // Assert
        Assert.True(harness.Gcs.Objects.ContainsKey((ApplicationStorageResolver.Bucket, expectedKey)));
        Assert.StartsWith("/shared-assets/pending/tenant-one/", part.UploadUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Empty(harness.Gcs.Names(ApplicationStorageResolver.Bucket, "pending/"));
    }

    /// <summary>Application keys isolate tenants sharing a bucket across the complete storage lifecycle.</summary>
    [Fact]
    public async Task ShouldPassConformanceGivenApplicationKeysAndSharedBucket()
    {
        // Arrange
        var harness = Harness.Create();
        harness.Gcs.BucketKeys[ApplicationStorageResolver.Bucket] = Harness.KeyOf("shared");
        var storage = new GcpObjectStorage(Harness.CreateClient(harness.Gcs), Harness.CreateSigner(),
            new ApplicationStorageResolver(Harness.KeyOf("shared")), harness.Protection.CreateProtector("layout"));

        // Act
        await ObjectStorageConformance.VerifyAsync(storage, new TenantId("tenant-one"), new TenantId("tenant-two"),
            (part, bytes, _) => ValueTask.FromResult(new ObjectPartReceipt(part.PartNumber,
                harness.Gcs.PutSigned(part.UploadUri, bytes.ToArray()))));

        // Assert
        Assert.Empty(harness.Gcs.Objects);
        Assert.Contains(harness.Gcs.Requests, static request => request.Path.Contains("/o/documents/tenant-two/", StringComparison.Ordinal));
    }

    /// <summary>Recovery checks the existing destination's tenant key before accepting or cleaning staging.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ShouldRejectUnprovenEncryptionGivenCompletionRecovery(bool replay, bool missingKey)
    {
        // Arrange
        var content = "object bytes"u8.ToArray();
        var digest = Digest(content);
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var signed = await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1));
        var receipt = new ObjectPartReceipt(1, harness.Gcs.PutSigned(signed.UploadUri, content));
        if (replay)
            await harness.Storage.CompleteUploadAsync(session, [receipt]);
        harness.Seed("tenant-one-bucket", digest, content);
        var key = ("tenant-one-bucket", $"content/sha256/{digest.Sha256[..2]}/{digest.Sha256}");
        var destination = harness.Gcs.Objects[key] with { KmsKey = missingKey ? null : Harness.KeyOf("tenant-two") };
        harness.Gcs.Objects[key] = destination;
        var previousRequests = harness.Gcs.Requests.Count;

        // Act
        var exception = await Record.ExceptionAsync(async () => await harness.Storage.CompleteUploadAsync(session, [receipt]));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Same(destination, harness.Gcs.Objects[key]);
        Assert.DoesNotContain(harness.Gcs.Requests.Skip(previousRequests), static request => request.Method == "DELETE");
        if (!replay)
            Assert.NotEmpty(harness.Gcs.Names("tenant-one-bucket", "staging/"));
    }

    const string Protector = "Cntryl.Portia.Storage.Gcp.UploadSession.v1";

    /// <summary>Checks tenant-keyed staging, verified digest metadata, and conditional immutable promotion.</summary>
    [Fact]
    public async Task ShouldPromoteVerifiedContentConditionallyGivenTenantKey()
    {
        // Arrange
        var content = "object bytes"u8.ToArray();
        var digest = Digest(content);
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var signed = await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(4));
        var receipt = harness.Gcs.PutSigned(signed.UploadUri, content);

        // Act
        await harness.Storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, receipt)]);

        // Assert
        var start = Assert.Single(harness.Gcs.Requests, static request => request.Method == "POST" && request.Path.StartsWith("upload/", StringComparison.Ordinal));
        Assert.Equal("upload/storage/v1/b/tenant-one-bucket/o", start.Path);
        Assert.Contains($"\"name\":\"content/sha256/{digest.Sha256[..2]}/{digest.Sha256}\"", start.Body, StringComparison.Ordinal);
        Assert.Equal("0", start.Query["ifGenerationMatch"]);
        Assert.Equal(Harness.KeyOf("tenant-one"), start.Query["kmsKeyName"]);
        var reads = harness.Gcs.Requests.Where(static request => request.Query.GetValueOrDefault("alt") == "media").ToArray();
        Assert.Equal(2, reads.Length);
        Assert.All(reads, read =>
        {
            Assert.StartsWith("storage/v1/b/tenant-one-bucket/o/staging/", read.Path, StringComparison.Ordinal);
            Assert.Equal(receipt, read.Query["generation"]);
        });
        var promoted = harness.Gcs.Objects[("tenant-one-bucket", $"content/sha256/{digest.Sha256[..2]}/{digest.Sha256}")];
        Assert.Equal(digest.Sha256, promoted.Metadata?["portia-sha256"]);
        Assert.Equal(digest.Length.ToString(CultureInfo.InvariantCulture), promoted.Metadata?["portia-length"]);
        Assert.Empty(harness.Gcs.Names("tenant-one-bucket", "staging/"));
    }

    /// <summary>Checks that signed part links are V4, bucket and staging scoped, and bounded by the requested lifetime.</summary>
    [Fact]
    public async Task ShouldSignBoundedPutUrlGivenPartRequest()
    {
        // Arrange
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), new ObjectDigest(new string('a', 64), 1));

        // Act
        var signed = await harness.Storage.SignPartAsync(session, 3, TimeSpan.FromMinutes(4));

        // Assert
        var query = HttpUtility.ParseQueryString(signed.UploadUri.Query);
        Assert.Equal(Uri.UriSchemeHttps, signed.UploadUri.Scheme);
        Assert.Equal("GOOG4-HMAC-SHA256", query["X-Goog-Algorithm"]);
        Assert.InRange(int.Parse(query["X-Goog-Expires"]!, CultureInfo.InvariantCulture), 235, 240);
        Assert.Matches("^/tenant-one-bucket/staging/[0-9a-f]{32}/part-00003$", signed.UploadUri.AbsolutePath);
        Assert.True(signed.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(4));
    }

    /// <summary>Checks that an opaque session token reveals neither the bucket nor the key reference.</summary>
    [Fact]
    public async Task ShouldKeepReferencesOutOfTokenGivenNewUpload()
    {
        // Arrange
        var harness = Harness.Create();

        // Act
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), new ObjectDigest(new string('a', 64), 1));

        // Assert
        Assert.DoesNotContain("tenant-one-bucket", session.Token, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-one-key", session.Token, StringComparison.Ordinal);
        Assert.DoesNotContain("staging", session.Token, StringComparison.Ordinal);
    }

    /// <summary>Checks that a staged full-object digest mismatch never reaches the content key.</summary>
    [Fact]
    public async Task ShouldRejectAndCleanUpGivenMismatchedStagedBytes()
    {
        // Arrange
        var harness = Harness.Create();
        var digest = Digest("correct"u8.ToArray());
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var signed = await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1));
        var receipt = harness.Gcs.PutSigned(signed.UploadUri, "changed"u8.ToArray());

        // Act
        var error = await Record.ExceptionAsync(async () =>
            await harness.Storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, receipt)]));

        // Assert
        Assert.IsType<InvalidDataException>(error);
        Assert.DoesNotContain(harness.Gcs.Requests, static request => request.Path.StartsWith("upload/", StringComparison.Ordinal));
        Assert.Empty(harness.Gcs.Names("tenant-one-bucket", ""));
    }

    /// <summary>Checks that an undersized nonfinal part is rejected and a corrected completion succeeds.</summary>
    [Fact]
    public async Task ShouldKeepSessionOpenGivenUndersizedNonfinalPart()
    {
        // Arrange
        var harness = Harness.Create();
        var first = new byte[ObjectStorageLimits.MinimumNonfinalPartLength];
        first.AsSpan().Fill(7);
        var second = "tail"u8.ToArray();
        var content = first.Concat(second).ToArray();
        var tenant = new TenantId("tenant-one");
        var session = await harness.Storage.CreateUploadAsync(tenant, Digest(content));
        var shortFirst = harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1))).UploadUri, first[..1024]);
        var secondReceipt = harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 2, TimeSpan.FromMinutes(1))).UploadUri, second);

        // Act
        var error = await Record.ExceptionAsync(async () => await harness.Storage.CompleteUploadAsync(
            session, [new ObjectPartReceipt(1, shortFirst), new ObjectPartReceipt(2, secondReceipt)]));

        // Assert
        Assert.IsType<InvalidDataException>(error);
        Assert.Equal(2, harness.Gcs.Names("tenant-one-bucket", "staging/").Length);
        var corrected = harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1))).UploadUri, first);
        await harness.Storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, corrected), new ObjectPartReceipt(2, secondReceipt)]);
        Assert.True(await harness.Storage.HeadAndVerifyAsync(tenant, Digest(content)));
    }

    /// <summary>Checks that a receipt that is not a generation is rejected without ending the session.</summary>
    [Fact]
    public async Task ShouldKeepSessionOpenGivenMalformedReceipt()
    {
        // Arrange
        var content = "receipt"u8.ToArray();
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), Digest(content));
        var receipt = harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1))).UploadUri, content);

        // Act
        var error = await Record.ExceptionAsync(async () =>
            await harness.Storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "\"etag\"")]));

        // Assert
        Assert.IsType<InvalidDataException>(error);
        Assert.Single(harness.Gcs.Names("tenant-one-bucket", "staging/"));
        await harness.Storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, receipt)]);
    }

    /// <summary>Checks that a stale receipt, from an overwritten part, is rejected and leaves the part for correction.</summary>
    [Fact]
    public async Task ShouldKeepSessionOpenGivenStaleReceipt()
    {
        // Arrange
        var content = "stale"u8.ToArray();
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), Digest(content));
        var uri = (await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1))).UploadUri;
        var stale = harness.Gcs.PutSigned(uri, "first attempt"u8.ToArray());
        harness.Gcs.PutSigned(uri, content);

        // Act
        var error = await Record.ExceptionAsync(async () =>
            await harness.Storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, stale)]));

        // Assert
        Assert.IsType<InvalidDataException>(error);
        Assert.Single(harness.Gcs.Names("tenant-one-bucket", "staging/"));
    }

    /// <summary>Checks that a lost completion response can be retried after immutable promotion.</summary>
    [Fact]
    public async Task ShouldTreatCompletionAsSuccessfulGivenRetryAfterPromotion()
    {
        // Arrange
        var content = "retryable object"u8.ToArray();
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), Digest(content));
        var receipt = harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1))).UploadUri, content);
        var parts = new[] { new ObjectPartReceipt(1, receipt) };
        await harness.Storage.CompleteUploadAsync(session, parts);

        // Act
        var error = await Record.ExceptionAsync(async () => await harness.Storage.CompleteUploadAsync(session, parts));

        // Assert
        Assert.Null(error);
        Assert.Single(harness.Gcs.Requests, static request => request.Method == "POST" && request.Path.StartsWith("upload/", StringComparison.Ordinal));
        Assert.Empty(harness.Gcs.Names("tenant-one-bucket", "staging/"));
    }

    /// <summary>Checks that a completion that lost a promotion race to a verified object still succeeds.</summary>
    [Fact]
    public async Task ShouldAcceptGivenVerifiedConditionalCreateRace()
    {
        // Arrange
        var content = "raced object"u8.ToArray();
        var digest = Digest(content);
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var receipt = harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1))).UploadUri, content);
        var contentKey = $"content/sha256/{digest.Sha256[..2]}/{digest.Sha256}";
        harness.Gcs.BeforeRewrite = gcs => gcs.Objects[("tenant-one-bucket", contentKey)] = new FakeGcsHandler.Stored(
            content, 5000, Harness.KeyOf("tenant-one"), new() { ["portia-sha256"] = digest.Sha256 });

        // Act
        var error = await Record.ExceptionAsync(async () =>
            await harness.Storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, receipt)]));

        // Assert
        Assert.Null(error);
        Assert.Equal(5000, harness.Gcs.Objects[("tenant-one-bucket", contentKey)].Generation);
        Assert.Empty(harness.Gcs.Names("tenant-one-bucket", "staging/"));
    }

    /// <summary>Checks that an unverified object at the immutable key is never accepted as the completed upload.</summary>
    [Fact]
    public async Task ShouldRejectGivenUnverifiedObjectAtContentKey()
    {
        // Arrange
        var content = "squatted key"u8.ToArray();
        var digest = Digest(content);
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var receipt = harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1))).UploadUri, content);
        harness.Gcs.BeforeRewrite = gcs => gcs.Objects[("tenant-one-bucket", $"content/sha256/{digest.Sha256[..2]}/{digest.Sha256}")] =
            new FakeGcsHandler.Stored("other"u8.ToArray(), 5000, null, null);

        // Act
        var error = await Record.ExceptionAsync(async () =>
            await harness.Storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, receipt)]));

        // Assert
        Assert.IsType<InvalidDataException>(error);
    }

    /// <summary>Checks that uploads and verification are isolated between tenants with the same digest.</summary>
    [Fact]
    public async Task ShouldIsolateObjectsGivenSameDigestForTwoTenants()
    {
        // Arrange
        var content = "shared digest"u8.ToArray();
        var digest = Digest(content);
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var receipt = harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1))).UploadUri, content);

        // Act
        await harness.Storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, receipt)]);

        // Assert
        Assert.True(await harness.Storage.HeadAndVerifyAsync(new TenantId("tenant-one"), digest));
        Assert.False(await harness.Storage.HeadAndVerifyAsync(new TenantId("tenant-two"), digest));
        Assert.Empty(harness.Gcs.Names("tenant-two-bucket", ""));
        Assert.DoesNotContain(harness.Gcs.Requests, static request => request.Path.Contains("tenant-two-bucket/o/staging", StringComparison.Ordinal));
    }

    /// <summary>Checks that a session token is bound to the Data Protection purpose of the provider.</summary>
    [Fact]
    public async Task ShouldRejectTokenGivenForeignProtectorPurpose()
    {
        // Arrange
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), new ObjectDigest(new string('a', 64), 1));
        var other = Harness.Create(purpose: "Cntryl.Portia.Storage.Aws.UploadSession.v1", sameKeyRingAs: harness);

        // Act
        var error = await Record.ExceptionAsync(async () => await other.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)));

        // Assert
        Assert.IsType<ArgumentException>(error);
    }

    /// <summary>Checks that an upload is refused when the tenant bucket does not default to the tenant key.</summary>
    [Fact]
    public async Task ShouldFailClosedGivenBucketDefaultKeyMismatch()
    {
        // Arrange
        var harness = Harness.Create();
        harness.Gcs.BucketKeys["tenant-one-bucket"] = Harness.KeyOf("someone-else");

        // Act
        var error = await Record.ExceptionAsync(async () =>
            await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), new ObjectDigest(new string('a', 64), 1)));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
    }

    /// <summary>Checks that a staged part not encrypted with the tenant key is never composed.</summary>
    [Fact]
    public async Task ShouldFailClosedGivenPartNotEncryptedWithTenantKey()
    {
        // Arrange
        var content = "wrong key"u8.ToArray();
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), Digest(content));
        harness.Gcs.SignedUploadKeyOverride = Harness.KeyOf("someone-else") + "/cryptoKeyVersions/1";
        var receipt = harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1))).UploadUri, content);

        // Act
        var error = await Record.ExceptionAsync(async () =>
            await harness.Storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, receipt)]));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.DoesNotContain(harness.Gcs.Requests, static request => request.Query.GetValueOrDefault("alt") == "media");
    }

    /// <summary>Checks that several large parts are assembled in part-number order and verify as one object.</summary>
    [Fact]
    public async Task ShouldAssembleInPartOrderGivenSeveralLargeParts()
    {
        // Arrange
        var harness = Harness.Create();
        var first = new byte[ObjectStorageLimits.MinimumNonfinalPartLength];
        first.AsSpan().Fill(3);
        var second = new byte[ObjectStorageLimits.MinimumNonfinalPartLength + 17];
        second.AsSpan().Fill(9);
        var tail = "tail"u8.ToArray();
        var content = first.Concat(second).Concat(tail).ToArray();
        var digest = Digest(content);
        var tenant = new TenantId("tenant-one");
        var session = await harness.Storage.CreateUploadAsync(tenant, digest);
        var receipts = new List<ObjectPartReceipt>();
        foreach (var (number, bytes) in new[] { (3, tail), (1, first), (2, second) })
        {
            var uri = (await harness.Storage.SignPartAsync(session, number, TimeSpan.FromMinutes(1))).UploadUri;
            receipts.Add(new ObjectPartReceipt(number, harness.Gcs.PutSigned(uri, bytes)));
        }

        // Act
        await harness.Storage.CompleteUploadAsync(session, receipts);

        // Assert
        Assert.True(await harness.Storage.HeadAndVerifyAsync(tenant, digest));
        Assert.Equal(content, harness.Gcs.Objects[("tenant-one-bucket", $"content/sha256/{digest.Sha256[..2]}/{digest.Sha256}")].Data);
    }

    /// <summary>Checks that an explicit abort removes every staged part.</summary>
    [Fact]
    public async Task ShouldRemoveStagingGivenAbort()
    {
        // Arrange
        var harness = Harness.Create();
        var session = await harness.Storage.CreateUploadAsync(new TenantId("tenant-one"), Digest("abort"u8.ToArray()));
        harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1))).UploadUri, "abort"u8.ToArray());
        harness.Gcs.PutSigned((await harness.Storage.SignPartAsync(session, 2, TimeSpan.FromMinutes(1))).UploadUri, "abort"u8.ToArray());

        // Act
        await harness.Storage.AbortUploadAsync(session);

        // Assert
        Assert.Empty(harness.Gcs.Names("tenant-one-bucket", ""));
    }

    /// <summary>Checks that download links are verified first and never outlive the maximum lifetime.</summary>
    [Fact]
    public async Task ShouldBoundDownloadLifetimeGivenVerifiedObject()
    {
        // Arrange
        var content = "download me"u8.ToArray();
        var digest = Digest(content);
        var tenant = new TenantId("tenant-one");
        var harness = Harness.Create();
        harness.Seed("tenant-one-bucket", digest, content);

        // Act
        var download = await harness.Storage.CreateDownloadAsync(tenant, digest, ObjectStorageLimits.MaximumDownloadLifetime);

        // Assert
        var query = HttpUtility.ParseQueryString(download.DownloadUri.Query);
        Assert.Equal(900, int.Parse(query["X-Goog-Expires"]!, CultureInfo.InvariantCulture));
        Assert.Contains($"/tenant-one-bucket/content/sha256/{digest.Sha256[..2]}/{digest.Sha256}", download.DownloadUri.AbsolutePath, StringComparison.Ordinal);
        Assert.True(download.ExpiresAt <= DateTimeOffset.UtcNow.Add(ObjectStorageLimits.MaximumDownloadLifetime));
        var tooLong = await Record.ExceptionAsync(async () =>
            await harness.Storage.CreateDownloadAsync(tenant, digest, ObjectStorageLimits.MaximumDownloadLifetime + TimeSpan.FromSeconds(1)));
        Assert.IsType<ArgumentOutOfRangeException>(tooLong);
    }

    /// <summary>Checks that a download link for a missing or unverified object is refused.</summary>
    [Fact]
    public async Task ShouldReportUnavailableGivenMissingOrUnverifiedContent()
    {
        // Arrange
        var content = "unverified"u8.ToArray();
        var digest = Digest(content);
        var harness = Harness.Create();
        harness.Gcs.Objects[("tenant-one-bucket", $"content/sha256/{digest.Sha256[..2]}/{digest.Sha256}")] =
            new FakeGcsHandler.Stored(content, 77, null, new() { ["portia-sha256"] = new string('0', 64) });

        // Act
        var error = await Record.ExceptionAsync(async () =>
            await harness.Storage.CreateDownloadAsync(new TenantId("tenant-one"), digest, TimeSpan.FromMinutes(1)));

        // Assert
        Assert.IsType<FileNotFoundException>(error);
        Assert.False(await harness.Storage.HeadAndVerifyAsync(new TenantId("tenant-one"), digest));
        Assert.Null(await harness.Storage.OpenReadAsync(new TenantId("tenant-one"), digest));
    }

    /// <summary>Checks that head verification reads metadata only and does not download the object.</summary>
    [Fact]
    public async Task ShouldVerifyMetadataOnlyGivenHeadAndVerify()
    {
        // Arrange
        var content = "head only"u8.ToArray();
        var digest = Digest(content);
        var harness = Harness.Create();
        harness.Seed("tenant-one-bucket", digest, content);

        // Act
        var verified = await harness.Storage.HeadAndVerifyAsync(new TenantId("tenant-one"), digest);

        // Assert
        Assert.True(verified);
        Assert.DoesNotContain(harness.Gcs.Requests, static request => request.Query.GetValueOrDefault("alt") == "media");
    }

    /// <summary>Checks that a verified object streams its bytes, pinned to the verified generation.</summary>
    [Fact]
    public async Task ShouldStreamVerifiedBytesGivenOpenRead()
    {
        // Arrange
        var content = "stream me"u8.ToArray();
        var digest = Digest(content);
        var harness = Harness.Create();
        harness.Seed("tenant-one-bucket", digest, content);

        // Act
        await using var stream = await harness.Storage.OpenReadAsync(new TenantId("tenant-one"), digest);

        // Assert
        Assert.NotNull(stream);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(content, copy.ToArray());
        var read = harness.Gcs.Requests.Last(static request => request.Query.GetValueOrDefault("alt") == "media");
        Assert.Equal("5000", read.Query["generation"]);
    }

    /// <summary>Checks that deleting an object removes it and that deleting it again is not an error.</summary>
    [Fact]
    public async Task ShouldDeleteIdempotentlyGivenRepeatedDelete()
    {
        // Arrange
        var content = "delete me"u8.ToArray();
        var digest = Digest(content);
        var tenant = new TenantId("tenant-one");
        var harness = Harness.Create();
        harness.Seed("tenant-one-bucket", digest, content);
        await harness.Storage.DeleteAsync(tenant, digest);

        // Act
        var error = await Record.ExceptionAsync(async () => await harness.Storage.DeleteAsync(tenant, digest));

        // Assert
        Assert.Null(error);
        Assert.False(await harness.Storage.HeadAndVerifyAsync(tenant, digest));
    }

    /// <summary>Checks that registration resolves the provider from an explicit client and signer.</summary>
    [Fact]
    public void ShouldRegisterProviderGivenClientAndSigner()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddSingleton<IObjectStorageTenantResolver>(new Harness.Resolver());
        services.AddGcpObjectStorage(options => options.UseClient(Harness.CreateClient(new FakeGcsHandler())).UseSigner(Harness.CreateSigner()));
        using var provider = services.BuildServiceProvider();

        // Act
        var storage = provider.GetRequiredService<IObjectStorage>();

        // Assert
        Assert.IsType<GcpObjectStorage>(storage);
    }

    /// <summary>Checks that registration fails closed when the client's credential cannot sign and no signer is given.</summary>
    [Fact]
    public void ShouldFailClosedGivenClientThatCannotSign()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddSingleton<IObjectStorageTenantResolver>(new Harness.Resolver());
        services.AddGcpObjectStorage(options => options.UseClient(Harness.CreateClient(new FakeGcsHandler())));
        using var provider = services.BuildServiceProvider();

        // Act
        var error = Record.Exception(() => provider.GetRequiredService<IObjectStorage>());

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("UseSigner", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Checks that registration requires a client.</summary>
    [Fact]
    public void ShouldRejectRegistrationGivenNoClient()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() => services.AddGcpObjectStorage(static _ => { }));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
    }

    static ObjectDigest Digest(byte[] content) => new(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);

    sealed class Harness
    {
        public required FakeGcsHandler Gcs { get; init; }

        public required GcpObjectStorage Storage { get; init; }

        public required IDataProtectionProvider Protection { get; init; }

        public static string KeyOf(string tenant) => $"projects/p/locations/l/keyRings/r/cryptoKeys/{tenant}-key";

        public static Harness Create(string purpose = Protector, Harness? sameKeyRingAs = null)
        {
            var gcs = sameKeyRingAs?.Gcs ?? new FakeGcsHandler();
            foreach (var tenant in new[] { "tenant-one", "tenant-two" })
            {
                gcs.BucketKeys[$"{tenant}-bucket"] = KeyOf(tenant);
            }

            var protection = sameKeyRingAs?.Protection ?? BuildProtection();
            return new Harness
            {
                Gcs = gcs,
                Protection = protection,
                Storage = new GcpObjectStorage(CreateClient(gcs), CreateSigner(), new Resolver(), protection.CreateProtector(purpose))
            };
        }

        public static StorageClientImpl CreateClient(FakeGcsHandler handler) => new(new StorageService(new BaseClientService.Initializer
        {
            ApplicationName = "portia-tests",
            GZipEnabled = false,
            HttpClientFactory = new HandlerFactory(handler)
        }));

        public static UrlSigner CreateSigner() => UrlSigner.FromBlobSigner(UrlSigner.HmacBlobSigner.Create("test-access-id", "test-secret"));

        public void Seed(string bucket, ObjectDigest digest, byte[] content) =>
            Gcs.Objects[(bucket, $"content/sha256/{digest.Sha256[..2]}/{digest.Sha256}")] = new FakeGcsHandler.Stored(
                content, 5000, KeyOf("tenant-one"), new() { ["portia-sha256"] = digest.Sha256 });

        static IDataProtectionProvider BuildProtection()
        {
            var services = new ServiceCollection();
            services.AddDataProtection();
            return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        }

        public sealed class Resolver : IObjectStorageTenantResolver
        {
            public string GetObjectKey(TenantId tenantId, ObjectDigest expected) =>
                $"content/sha256/{expected.Sha256[..2]}/{expected.Sha256}";

            public string GetUploadKey(TenantId tenantId, Uuid uploadId) => $"staging/{uploadId:N}";

            public ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
                ValueTask.FromResult(new ObjectStorageLocation($"{tenantId.Value}-bucket", KeyOf(tenantId.Value)));
        }

        sealed class HandlerFactory(FakeGcsHandler handler) : Google.Apis.Http.HttpClientFactory
        {
            protected override HttpMessageHandler CreateHandler(Google.Apis.Http.CreateHttpClientArgs args) => handler;
        }
    }
}

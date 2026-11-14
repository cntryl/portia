using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using System.Xml.Linq;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using Cntryl.Portia.Storage.Azure;

namespace Cntryl.Portia.Tests.Storage;

/// <summary>Checks Azure Blob request construction and object verification without cloud credentials or network.</summary>
public sealed class AzureObjectStorageTests
{
    /// <summary>Completion preserves application-chosen keys even if the resolver changes.</summary>
    [Fact]
    public async Task ShouldPinApplicationLayoutGivenResolverChangesDuringUpload()
    {
        // Arrange
        var content = "application owned keys"u8.ToArray();
        var digest = DigestOf(content);
        var tenant = new TenantId("tenant-one");
        var resolver = new ApplicationStorageResolver("shared-scope");
        var expectedKey = resolver.GetObjectKey(tenant, digest);
        var service = new FakeBlobService();
        var storage = new AzureObjectStorage(CreateClient(service), resolver,
            new EphemeralDataProtectionProvider().CreateProtector("layout"));
        var session = await storage.CreateUploadAsync(tenant, digest);
        var part = await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1));
        var receipt = await service.UploadAsync(part, content);
        resolver.ContentRoot = "moved";
        resolver.UploadRoot = "new-uploads";

        // Act
        await storage.CompleteUploadAsync(session, [receipt]);

        // Assert
        Assert.True(service.Contains($"/{ApplicationStorageResolver.Bucket}/{expectedKey}"));
        Assert.StartsWith($"/{ApplicationStorageResolver.Bucket}/pending/tenant-one/", part.UploadUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(1, service.BlobCount);
    }

    /// <summary>Application keys isolate tenants sharing a container across the complete storage lifecycle.</summary>
    [Fact]
    public async Task ShouldPassConformanceGivenApplicationKeysAndSharedContainer()
    {
        // Arrange
        var service = new FakeBlobService();
        var storage = new AzureObjectStorage(CreateClient(service), new ApplicationStorageResolver("shared-scope"),
            new EphemeralDataProtectionProvider().CreateProtector("layout"));

        // Act
        await ObjectStorageConformance.VerifyAsync(storage, new TenantId("tenant-one"), new TenantId("tenant-two"),
            async (part, bytes, _) => await service.UploadAsync(part, bytes.ToArray()));

        // Assert
        Assert.Equal(0, service.BlobCount);
        Assert.All(service.Requests, static request => Assert.StartsWith("/shared-assets/", request.Path, StringComparison.Ordinal));
        Assert.Contains(service.Requests, static request => request.Path.StartsWith("/shared-assets/documents/tenant-two/", StringComparison.Ordinal));
    }

    /// <summary>A replacement between metadata verification and download must never be streamed.</summary>
    [Fact]
    public async Task ShouldReturnUnavailableGivenBlobReplacedAfterVerification()
    {
        // Arrange
        var content = "verified"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var path = $"/{FirstContainer}/content/sha256/{digest.Sha256[..2]}/{digest.Sha256}";
        service.SeedBlob(path, content, digest.Sha256);
        service.AfterHead = () => service.SeedBlob(path, "tampered"u8.ToArray(), new string('0', 64));

        // Act
        await using var stream = await storage.OpenReadAsync(new TenantId("tenant-one"), digest);

        // Assert
        Assert.Null(stream);
        Assert.Single(service.Requests, static request => request.Method == HttpMethod.Get);
    }

    const string FirstContainer = "tenant-one-container";
    const string FirstScope = "tenant-one-scope";

    /// <summary>Checks the tenant container, encryption scope, staging name, and conditional immutable promotion.</summary>
    [Fact]
    public async Task ShouldPromoteVerifiedContentConditionallyGivenTenantEncryptionScope()
    {
        // Arrange
        var content = "object bytes"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var tenant = new TenantId("tenant-one");
        var session = await storage.CreateUploadAsync(tenant, digest);
        var signed = await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(4));
        var receipt = await service.UploadAsync(signed, content);

        // Act
        await storage.CompleteUploadAsync(session, [receipt]);

        // Assert
        Assert.DoesNotContain(FirstContainer, session.Token, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstScope, session.Token, StringComparison.Ordinal);
        Assert.DoesNotContain("staging", session.Token, StringComparison.Ordinal);
        var query = HttpUtility.ParseQueryString(signed.UploadUri.Query);
        Assert.StartsWith("/" + FirstContainer + "/staging/", signed.UploadUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("w", query["sp"]);
        Assert.Equal(FirstScope, query["ses"]);
        Assert.Equal("https", signed.UploadUri.Scheme);
        Assert.Equal("block", query["comp"]);
        Assert.Equal(receipt.Token, query["blockid"]);
        Assert.True(signed.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(4));

        var commit = Assert.Single(service.Requests, static r => r.Method == HttpMethod.Put && r.Query.Contains("comp=blocklist", StringComparison.Ordinal));
        Assert.Equal(FirstScope, commit.Headers["x-ms-encryption-scope"]);
        Assert.Equal(digest.Sha256, commit.Headers["x-ms-meta-portiasha256"]);
        var copy = Assert.Single(service.Requests, static r => r.Headers.ContainsKey("x-ms-copy-source"));
        var contentPath = $"/{FirstContainer}/content/sha256/{digest.Sha256[..2]}/{digest.Sha256}";
        Assert.Equal(contentPath, copy.Path);
        Assert.Equal(FirstScope, copy.Headers["x-ms-encryption-scope"]);
        Assert.Equal("*", copy.Headers["if-none-match"]);
        Assert.Equal(service.EtagOf(commit.Path), copy.Headers["x-ms-source-if-match"]);
        Assert.Equal(digest.Sha256, copy.Headers["x-ms-meta-portiasha256"]);
        Assert.Equal(digest.Length.ToString(CultureInfo.InvariantCulture), copy.Headers["x-ms-meta-portialength"]);
        var sourceQuery = HttpUtility.ParseQueryString(new Uri(copy.Headers["x-ms-copy-source"]).Query);
        Assert.Equal("r", sourceQuery["sp"]);
        Assert.True(service.Contains(contentPath));
        Assert.False(service.Contains(commit.Path));
    }

    /// <summary>Checks that a staged full-object digest mismatch never reaches the content key.</summary>
    [Fact]
    public async Task ShouldRejectAndCleanUpStagingGivenMismatchedStagedBytes()
    {
        // Arrange
        var digest = DigestOf("correct"u8.ToArray());
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), "changed"u8.ToArray());

        // Act
        var exception = await Record.ExceptionAsync(async () => await storage.CompleteUploadAsync(session, [receipt]));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
        Assert.DoesNotContain(service.Requests, static r => r.Headers.ContainsKey("x-ms-copy-source"));
        Assert.False(service.Contains($"/{FirstContainer}/content/sha256/{digest.Sha256[..2]}/{digest.Sha256}"));
        Assert.Equal(0, service.BlobCount);
    }

    /// <summary>Checks that content longer than declared ends the session without committing the staged blocks.</summary>
    [Fact]
    public async Task ShouldRejectAndDiscardBlocksGivenLengthDifferentFromDeclared()
    {
        // Arrange
        var content = "declared"u8.ToArray();
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), DigestOf(content));
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), "declared plus extra"u8.ToArray());

        // Act
        var exception = await Record.ExceptionAsync(async () => await storage.CompleteUploadAsync(session, [receipt]));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
        Assert.Equal(0, service.UncommittedBlockCount);
        Assert.Equal(0, service.BlobCount);
    }

    /// <summary>Checks that Azure's missing minimum block size is enforced and the session stays open.</summary>
    [Fact]
    public async Task ShouldKeepSessionOpenGivenUndersizedNonfinalPart()
    {
        // Arrange
        var first = new byte[1024];
        var second = new byte[ObjectStorageLimits.MinimumNonfinalPartLength];
        RandomNumberGenerator.Fill(first);
        RandomNumberGenerator.Fill(second);
        var whole = first.Concat(second).ToArray();
        var digest = DigestOf(whole);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var one = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), first);
        var two = await service.UploadAsync(await storage.SignPartAsync(session, 2, TimeSpan.FromMinutes(1)), second);

        // Act
        var exception = await Record.ExceptionAsync(async () => await storage.CompleteUploadAsync(session, [one, two]));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
        Assert.DoesNotContain(service.Requests, static r => r.Query.Contains("comp=blocklist", StringComparison.Ordinal) && r.Method == HttpMethod.Put);
        Assert.DoesNotContain(service.Requests, static r => r.Method == HttpMethod.Delete);
        Assert.Equal(2, service.UncommittedBlockCount);
    }

    /// <summary>Checks that a rejected receipt leaves the session open for a corrected completion.</summary>
    [Fact]
    public async Task ShouldAcceptCorrectedCompletionGivenRejectedReceipt()
    {
        // Arrange
        var content = "two attempts"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);
        var rejected = await Record.ExceptionAsync(async () =>
            await storage.CompleteUploadAsync(session, [new ObjectPartReceipt(1, "forged")]));

        // Act
        await storage.CompleteUploadAsync(session, [receipt]);

        // Assert
        Assert.IsType<InvalidDataException>(rejected);
        Assert.True(await storage.HeadAndVerifyAsync(new TenantId("tenant-one"), digest));
    }

    /// <summary>Checks that a block that was never uploaded is rejected as a bad receipt.</summary>
    [Fact]
    public async Task ShouldRejectReceiptGivenBlockNeverUploaded()
    {
        // Arrange
        var content = "uploaded"u8.ToArray();
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), DigestOf(content));
        var uploaded = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);
        var missing = (await storage.SignPartAsync(session, 2, TimeSpan.FromMinutes(1))).UploadUri;
        var missingToken = HttpUtility.ParseQueryString(missing.Query)["blockid"]!;

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await storage.CompleteUploadAsync(session, [uploaded, new ObjectPartReceipt(2, missingToken)]));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
        Assert.Equal(1, service.UncommittedBlockCount);
    }

    /// <summary>Checks that a lost completion response can be retried after immutable promotion.</summary>
    [Fact]
    public async Task ShouldTreatPromotionAsSuccessfulGivenVerifiedPromotionAndRetriedCompletion()
    {
        // Arrange
        var content = "retryable object"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);
        await storage.CompleteUploadAsync(session, [receipt]);

        // Act
        var exception = await Record.ExceptionAsync(async () => await storage.CompleteUploadAsync(session, [receipt]));

        // Assert
        Assert.Null(exception);
        Assert.Single(service.Requests, static r => r.Headers.ContainsKey("x-ms-copy-source"));
        Assert.Single(service.Requests, static r => r.Method == HttpMethod.Put && r.Query.Contains("comp=blocklist", StringComparison.Ordinal));
    }

    /// <summary>Checks that completion resumes from committed staging without recommitting after a failed promotion.</summary>
    [Fact]
    public async Task ShouldResumePromotionGivenCommittedStagingAndFailedCopy()
    {
        // Arrange
        var content = "committed before copy failed"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);
        service.FailNextCopy = true;
        var failed = await Record.ExceptionAsync(async () => await storage.CompleteUploadAsync(session, [receipt]));

        // Act
        await storage.CompleteUploadAsync(session, [receipt]);

        // Assert
        Assert.IsType<RequestFailedException>(failed);
        Assert.Single(service.Requests, static r => r.Method == HttpMethod.Put && r.Query.Contains("comp=blocklist", StringComparison.Ordinal));
        Assert.True(await storage.HeadAndVerifyAsync(new TenantId("tenant-one"), digest));
        Assert.Equal(1, service.BlobCount);
    }

    /// <summary>Checks that a destination created by a racing completion counts as success.</summary>
    [Fact]
    public async Task ShouldAcceptGivenVerifiedDestinationCreatedByRacingCompletion()
    {
        // Arrange
        var content = "raced object"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);
        service.SeedBlob($"/{FirstContainer}/content/sha256/{digest.Sha256[..2]}/{digest.Sha256}", content, digest.Sha256);

        // Act
        var exception = await Record.ExceptionAsync(async () => await storage.CompleteUploadAsync(session, [receipt]));

        // Assert
        Assert.Null(exception);
        Assert.Single(service.Requests, static r => r.Headers.ContainsKey("x-ms-copy-source"));
        Assert.Equal(1, service.BlobCount);
    }

    /// <summary>Checks that an unverified blob at the immutable key is reported as corruption.</summary>
    [Fact]
    public async Task ShouldRejectGivenUnverifiedDestinationAtContentKey()
    {
        // Arrange
        var content = "expected object"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);
        service.SeedBlob($"/{FirstContainer}/content/sha256/{digest.Sha256[..2]}/{digest.Sha256}", "impostor"u8.ToArray(), "0".PadLeft(64, '0'));

        // Act
        var exception = await Record.ExceptionAsync(async () => await storage.CompleteUploadAsync(session, [receipt]));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
    }

    /// <summary>Checks that simultaneous completions of one session all report success and promote once.</summary>
    [Fact]
    public async Task ShouldPromoteOnceGivenConcurrentCompletions()
    {
        // Arrange
        var content = new byte[64 * 1024];
        RandomNumberGenerator.Fill(content);
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var tenant = new TenantId("tenant-one");
        var session = await storage.CreateUploadAsync(tenant, digest);
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            await Record.ExceptionAsync(async () => await storage.CompleteUploadAsync(session, [receipt])))));

        // Assert
        Assert.All(results, Assert.Null);
        Assert.True(await storage.HeadAndVerifyAsync(tenant, digest));
        Assert.Equal(1, service.BlobCount);
    }

    /// <summary>Checks that cancellation before completion does not discard possibly committed staging.</summary>
    [Fact]
    public async Task ShouldPreserveStagingGivenCompletionCanceledBeforeRequest()
    {
        // Arrange
        var content = "cancel me"u8.ToArray();
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), DigestOf(content));
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var exception = await Record.ExceptionAsync(async () => await storage.CompleteUploadAsync(session, [receipt], cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.DoesNotContain(service.Requests, static r => r.Method == HttpMethod.Delete);
        Assert.Equal(1, service.UncommittedBlockCount);
    }

    /// <summary>Checks that tenants resolve to separate containers and never see each other's objects.</summary>
    [Fact]
    public async Task ShouldIsolateObjectsGivenSameDigestForDifferentTenants()
    {
        // Arrange
        var content = "tenant isolation"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var owner = new TenantId("tenant-one");
        var other = new TenantId("tenant-two");
        var session = await storage.CreateUploadAsync(owner, digest);
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);
        await storage.CompleteUploadAsync(session, [receipt]);

        // Act
        var otherVisible = await storage.HeadAndVerifyAsync(other, digest);

        // Assert
        Assert.True(await storage.HeadAndVerifyAsync(owner, digest));
        Assert.False(otherVisible);
        Assert.Null(await storage.OpenReadAsync(other, digest));
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await storage.CreateDownloadAsync(other, digest, TimeSpan.FromMinutes(1)));
        Assert.Contains(service.Requests, static r => r.Path.StartsWith("/tenant-two-container/", StringComparison.Ordinal));
        Assert.DoesNotContain(service.Requests, r => r.Path.StartsWith("/tenant-two-container/", StringComparison.Ordinal) && r.Method != HttpMethod.Head);
    }

    /// <summary>Checks that a token cannot be reused to sign parts for another provider key ring.</summary>
    [Fact]
    public async Task ShouldRejectTokenGivenDifferentProtectorOrGarbage()
    {
        // Arrange
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var foreign = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), DigestOf("x"u8.ToArray()));

        // Act
        var foreignKeyRing = await Record.ExceptionAsync(async () => await foreign.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)));
        var garbage = await Record.ExceptionAsync(async () => await storage.SignPartAsync(new ObjectUploadSession("not-a-token"), 1, TimeSpan.FromMinutes(1)));

        // Assert
        Assert.IsType<ArgumentException>(foreignKeyRing);
        Assert.IsType<ArgumentException>(garbage);
    }

    /// <summary>Checks that download links are bounded, read-only, and short lived.</summary>
    [Fact]
    public async Task ShouldBoundDownloadLifetimeGivenRequestedLifetime()
    {
        // Arrange
        var content = "download me"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var tenant = new TenantId("tenant-one");
        service.SeedBlob($"/{FirstContainer}/content/sha256/{digest.Sha256[..2]}/{digest.Sha256}", content, digest.Sha256);

        // Act
        var download = await storage.CreateDownloadAsync(tenant, digest, TimeSpan.FromMinutes(5));

        // Assert
        var tooLong = await Record.ExceptionAsync(async () =>
            await storage.CreateDownloadAsync(tenant, digest, ObjectStorageLimits.MaximumDownloadLifetime + TimeSpan.FromSeconds(1)));
        var zero = await Record.ExceptionAsync(async () => await storage.CreateDownloadAsync(tenant, digest, TimeSpan.Zero));
        Assert.IsType<ArgumentOutOfRangeException>(tooLong);
        Assert.IsType<ArgumentOutOfRangeException>(zero);
        Assert.InRange(download.ExpiresAt, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(5));
        var query = HttpUtility.ParseQueryString(download.DownloadUri.Query);
        Assert.Equal("r", query["sp"]);
        Assert.Equal("https", query["spr"]);
        Assert.Equal(download.ExpiresAt.UtcDateTime, DateTimeOffset.Parse(query["se"]!, CultureInfo.InvariantCulture).UtcDateTime, TimeSpan.FromSeconds(1));
    }

    /// <summary>Checks that part links cannot outlive the 24-hour upload session or exceed the lifetime bound.</summary>
    [Fact]
    public async Task ShouldRejectPartLifetimeGivenLifetimeBeyondBound()
    {
        // Arrange
        var storage = CreateStorage(new FakeBlobService());
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), DigestOf("x"u8.ToArray()));

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await storage.SignPartAsync(session, 1, ObjectStorageLimits.MaximumDownloadLifetime + TimeSpan.FromSeconds(1)));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    /// <summary>Checks that metadata verification never downloads the object.</summary>
    [Fact]
    public async Task ShouldVerifyWithoutDownloadGivenMetadataAndLengthMatch()
    {
        // Arrange
        var content = "head only"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        service.SeedBlob($"/{FirstContainer}/content/sha256/{digest.Sha256[..2]}/{digest.Sha256}", content, digest.Sha256);

        // Act
        var verified = await storage.HeadAndVerifyAsync(new TenantId("tenant-one"), digest);

        // Assert
        Assert.True(verified);
        Assert.All(service.Requests, static r => Assert.Equal(HttpMethod.Head, r.Method));
    }

    /// <summary>Checks that missing, wrong-length, and unverified-metadata objects are all reported unavailable.</summary>
    [Fact]
    public async Task ShouldReportUnavailableGivenMissingOrUnverifiedContent()
    {
        // Arrange
        var content = "stored"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var tenant = new TenantId("tenant-one");
        var wrongLength = new ObjectDigest(digest.Sha256, content.LongLength + 1);
        service.SeedBlob($"/{FirstContainer}/content/sha256/{digest.Sha256[..2]}/{digest.Sha256}", content, new string('f', 64));

        // Act
        var unverifiedMetadata = await storage.HeadAndVerifyAsync(tenant, digest);

        // Assert
        Assert.False(unverifiedMetadata);
        Assert.False(await storage.HeadAndVerifyAsync(tenant, wrongLength));
        Assert.False(await storage.HeadAndVerifyAsync(tenant, DigestOf("absent"u8.ToArray())));
        Assert.Null(await storage.OpenReadAsync(tenant, digest));
    }

    /// <summary>Checks that a verified object streams back byte for byte and can be deleted.</summary>
    [Fact]
    public async Task ShouldStreamAndDeleteGivenVerifiedObject()
    {
        // Arrange
        var content = "stream and delete"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var tenant = new TenantId("tenant-one");
        service.SeedBlob($"/{FirstContainer}/content/sha256/{digest.Sha256[..2]}/{digest.Sha256}", content, digest.Sha256);

        // Act
        await using var stream = await storage.OpenReadAsync(tenant, digest);
        using var copy = new MemoryStream();
        await stream!.CopyToAsync(copy);
        await storage.DeleteAsync(tenant, digest);
        await storage.DeleteAsync(tenant, digest);

        // Assert
        Assert.Equal(content, copy.ToArray());
        Assert.False(await storage.HeadAndVerifyAsync(tenant, digest));
    }

    /// <summary>Checks that abort discards uncommitted blocks and committed staging, and is idempotent.</summary>
    [Fact]
    public async Task ShouldDiscardStagingGivenAbortedUpload()
    {
        // Arrange
        var content = "abort me"u8.ToArray();
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), DigestOf(content));
        await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);

        // Act
        await storage.AbortUploadAsync(session);
        await storage.AbortUploadAsync(session);

        // Assert
        Assert.Equal(0, service.UncommittedBlockCount);
        Assert.Equal(0, service.BlobCount);
    }

    /// <summary>Checks that a failed staging cleanup after a decided outcome does not replace that outcome.</summary>
    [Fact]
    public async Task ShouldReportCommittedCompletionGivenStagingCleanupFailure()
    {
        // Arrange
        var content = "cleanup fails"u8.ToArray();
        var digest = DigestOf(content);
        var service = new FakeBlobService();
        var storage = CreateStorage(service);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), digest);
        var receipt = await service.UploadAsync(await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)), content);
        service.FailDeletes = true;

        // Act
        var exception = await Record.ExceptionAsync(async () => await storage.CompleteUploadAsync(session, [receipt]));

        // Assert
        Assert.Null(exception);
        Assert.True(await storage.HeadAndVerifyAsync(new TenantId("tenant-one"), digest));
    }

    /// <summary>Checks that an Entra ID client signs links with a user delegation key.</summary>
    [Fact]
    public async Task ShouldSignWithUserDelegationKeyGivenTokenCredentialClient()
    {
        // Arrange
        var service = new FakeBlobService();
        var client = new BlobServiceClient(new Uri("https://account.blob.core.windows.net"), new StaticTokenCredential(), ClientOptions(service));
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), DigestOf("x"u8.ToArray()));

        // Act
        var first = await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(2));
        var second = await storage.SignPartAsync(session, 2, TimeSpan.FromMinutes(2));

        // Assert
        var query = HttpUtility.ParseQueryString(first.UploadUri.Query);
        Assert.Equal("w", query["sp"]);
        Assert.Equal(FirstScope, query["ses"]);
        Assert.False(string.IsNullOrEmpty(query["skoid"]));
        Assert.False(string.IsNullOrEmpty(query["sig"]));
        Assert.NotNull(second);
        Assert.Single(service.Requests, static r => r.Method == HttpMethod.Post && r.Query.Contains("comp=userdelegationkey", StringComparison.Ordinal));
    }

    /// <summary>Checks that a client unable to sign fails closed with a clear error.</summary>
    [Fact]
    public async Task ShouldFailClosedGivenClientThatCannotSignLinks()
    {
        // Arrange
        var service = new FakeBlobService { DenyDelegationKey = true };
        var client = new BlobServiceClient(new Uri("https://account.blob.core.windows.net"), new StaticTokenCredential(), ClientOptions(service));
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), DigestOf("x"u8.ToArray()));

        // Act
        var exception = await Record.ExceptionAsync(async () => await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Checks that a plain-HTTP emulator endpoint receives links its scheme can use.</summary>
    [Fact]
    public async Task ShouldAllowHttpLinksGivenHttpEndpoint()
    {
        // Arrange
        var service = new FakeBlobService();
        var client = new BlobServiceClient(new Uri("http://127.0.0.1:10000/devstoreaccount1"),
            new StorageSharedKeyCredential("devstoreaccount1", Convert.ToBase64String(new byte[32])), ClientOptions(service));
        var storage = CreateStorage(client);
        var session = await storage.CreateUploadAsync(new TenantId("tenant-one"), DigestOf("x"u8.ToArray()));

        // Act
        var signed = await storage.SignPartAsync(session, 1, TimeSpan.FromMinutes(1));

        // Assert
        Assert.Equal("http", signed.UploadUri.Scheme);
        Assert.Equal("https,http", HttpUtility.ParseQueryString(signed.UploadUri.Query)["spr"]);
    }

    /// <summary>Checks that resolver output missing either reference is refused.</summary>
    [Fact]
    public async Task ShouldRefuseGivenResolverWithoutEncryptionScope()
    {
        // Arrange
        var service = new FakeBlobService();
        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<IObjectStorageTenantResolver>(new FixedResolver(new ObjectStorageLocation("container", " ")));
        services.AddAzureObjectStorage(builder => builder.UseClient(CreateClient(service)));
        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IObjectStorage>();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await storage.CreateUploadAsync(new TenantId("tenant-one"), DigestOf("x"u8.ToArray())));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Checks that registration without a client fails when configured.</summary>
    [Fact]
    public void ShouldRejectRegistrationGivenNoClient()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var exception = Record.Exception(() => services.AddAzureObjectStorage(static _ => { }));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    static ObjectDigest DigestOf(byte[] content) => new(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);

    static BlobClientOptions ClientOptions(FakeBlobService service) => new()
    {
        Transport = new HttpClientTransport(new HttpClient(service)),
        Retry = { MaxRetries = 0 }
    };

    static BlobServiceClient CreateClient(FakeBlobService service) => new(
        new Uri("https://account.blob.core.windows.net"),
        new StorageSharedKeyCredential("account", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
        ClientOptions(service));

    static AzureObjectStorage CreateStorage(FakeBlobService service) => CreateStorage(CreateClient(service));

    static AzureObjectStorage CreateStorage(BlobServiceClient client)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<IObjectStorageTenantResolver>(new TenantContainerResolver());
        services.AddAzureObjectStorage(builder => builder.UseClient(client));
        var provider = services.BuildServiceProvider();
        return (AzureObjectStorage)provider.GetRequiredService<IObjectStorage>();
    }

    /// <summary>Maps each tenant to its own container and encryption scope.</summary>
    sealed class TenantContainerResolver : IObjectStorageTenantResolver
    {
        public string GetObjectKey(TenantId tenantId, ObjectDigest expected) =>
            $"content/sha256/{expected.Sha256[..2]}/{expected.Sha256}";

        public string GetUploadKey(TenantId tenantId, Uuid uploadId) => $"staging/{uploadId:N}";

        public ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(new ObjectStorageLocation($"{tenantId.Value}-container", $"{tenantId.Value}-scope"));
    }

    /// <summary>Returns one fixed location for every tenant.</summary>
    sealed class FixedResolver(ObjectStorageLocation location) : IObjectStorageTenantResolver
    {
        public string GetObjectKey(TenantId tenantId, ObjectDigest expected) =>
            $"content/sha256/{expected.Sha256[..2]}/{expected.Sha256}";

        public string GetUploadKey(TenantId tenantId, Uuid uploadId) => $"staging/{uploadId:N}";

        public ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(location);
    }

    /// <summary>A credential that issues a fixed token, so no identity provider is contacted.</summary>
    sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    /// <summary>A captured outgoing blob service request.</summary>
    sealed record RecordedRequest(HttpMethod Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers);

    /// <summary>A small in-memory Azure Blob service that answers the block blob operations the adapter uses.</summary>
    sealed class FakeBlobService : HttpMessageHandler
    {
        readonly object _sync = new();
        readonly Dictionary<string, StoredBlob> _blobs = [with(StringComparer.Ordinal)];
        readonly Dictionary<string, Dictionary<string, byte[]>> _uncommitted = [with(StringComparer.Ordinal)];
        readonly List<RecordedRequest> _requests = [];
        readonly Dictionary<string, string> _etagHistory = [with(StringComparer.Ordinal)];
        int _etagCounter;

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_sync)
                {
                    return [.. _requests];
                }
            }
        }

        public bool FailNextCopy { get; set; }

        public Action? AfterHead { get; set; }

        public bool FailDeletes { get; set; }

        public bool DenyDelegationKey { get; set; }

        public int BlobCount
        {
            get
            {
                lock (_sync)
                {
                    return _blobs.Count;
                }
            }
        }

        public int UncommittedBlockCount
        {
            get
            {
                lock (_sync)
                {
                    return _uncommitted.Values.Sum(static blocks => blocks.Count);
                }
            }
        }

        public bool Contains(string path)
        {
            lock (_sync)
            {
                return _blobs.ContainsKey(path);
            }
        }

        public string EtagOf(string path)
        {
            lock (_sync)
            {
                return _etagHistory[path];
            }
        }

        public void SeedBlob(string path, byte[] content, string digestMetadata)
        {
            lock (_sync)
            {
                _blobs[path] = new StoredBlob(content, NextEtag(), new Dictionary<string, string> { ["portiasha256"] = digestMetadata });
            }
        }

        /// <summary>Plays the client role: PUTs bytes to the signed URL and returns the receipt a client would derive from it.</summary>
        public async Task<ObjectPartReceipt> UploadAsync(ObjectUploadPart part, byte[] bytes)
        {
            using var client = new HttpClient(this, disposeHandler: false);
            using var request = new HttpRequestMessage(HttpMethod.Put, part.UploadUri) { Content = new ByteArrayContent(bytes) };
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return new ObjectPartReceipt(part.PartNumber, HttpUtility.ParseQueryString(part.UploadUri.Query)["blockid"]!);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var uri = request.RequestUri!;
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            var query = HttpUtility.ParseQueryString(uri.Query);
            lock (_sync)
            {
                _requests.Add(new RecordedRequest(request.Method, path, uri.Query, headers));
                return Handle(request.Method, path, query, headers, body);
            }
        }

        HttpResponseMessage Handle(HttpMethod method, string path, System.Collections.Specialized.NameValueCollection query,
            Dictionary<string, string> headers, byte[] body)
        {
            var comp = query["comp"];
            if (method == HttpMethod.Post && comp == "userdelegationkey")
            {
                return DenyDelegationKey ? Error(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch") : DelegationKey(body);
            }

            if (method == HttpMethod.Put && comp == "block")
            {
                var blocks = _uncommitted.TryGetValue(path, out var existing) ? existing : _uncommitted[path] = [with(StringComparer.Ordinal)];
                blocks[query["blockid"]!] = body;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (method == HttpMethod.Get && comp == "blocklist")
            {
                _blobs.TryGetValue(path, out var blob);
                _uncommitted.TryGetValue(path, out var pending);
                if (blob is null && (pending is null || pending.Count == 0))
                {
                    return Error(HttpStatusCode.NotFound, "BlobNotFound");
                }

                return Xml(HttpStatusCode.OK, new XElement("BlockList",
                    new XElement("CommittedBlocks", (blob?.Blocks ?? []).Select(block =>
                        new XElement("Block", new XElement("Name", block.Key), new XElement("Size", block.Value.Length)))),
                    new XElement("UncommittedBlocks", (pending ?? []).Select(block =>
                        new XElement("Block", new XElement("Name", block.Key), new XElement("Size", block.Value.Length))))));
            }

            if (method == HttpMethod.Put && comp == "blocklist")
            {
                var ids = XDocument.Parse(Encoding.UTF8.GetString(body).TrimStart('\uFEFF')).Root!.Elements().Select(static e => e.Value).ToArray();
                _blobs.TryGetValue(path, out var current);
                if (current is not null && headers.TryGetValue("If-None-Match", out var condition) && condition == "*")
                {
                    return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                }
                _uncommitted.TryGetValue(path, out var pending);
                var resolved = new List<KeyValuePair<string, byte[]>>();
                foreach (var id in ids)
                {
                    if (pending is not null && pending.TryGetValue(id, out var bytes))
                    {
                        resolved.Add(new(id, bytes));
                    }
                    else if (current is not null && current.Blocks.TryGetValue(id, out bytes))
                    {
                        resolved.Add(new(id, bytes));
                    }
                    else
                    {
                        return Error(HttpStatusCode.BadRequest, "InvalidBlockList");
                    }
                }

                _uncommitted.Remove(path);
                var meta = headers.Where(static h => h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(static h => h.Key["x-ms-meta-".Length..], static h => h.Value, StringComparer.OrdinalIgnoreCase);
                var stored = new StoredBlob(resolved.SelectMany(static b => b.Value).ToArray(), NextEtag(), meta) { Blocks = new Dictionary<string, byte[]>(resolved, StringComparer.Ordinal) };
                _blobs[path] = stored;
                _etagHistory[path] = stored.ETag;
                return Etagged(HttpStatusCode.Created, stored);
            }

            if (method == HttpMethod.Put && headers.TryGetValue("x-ms-copy-source", out var copySource))
            {
                if (FailNextCopy)
                {
                    FailNextCopy = false;
                    return Error(HttpStatusCode.InternalServerError, "InternalError");
                }

                if (_blobs.ContainsKey(path))
                {
                    return Error(HttpStatusCode.Conflict, "BlobAlreadyExists");
                }

                var sourcePath = Uri.UnescapeDataString(new Uri(copySource).AbsolutePath);
                if (!_blobs.TryGetValue(sourcePath, out var source))
                {
                    return Error(HttpStatusCode.NotFound, "CannotVerifyCopySource");
                }

                if (headers.TryGetValue("x-ms-source-if-match", out var match) && match != source.ETag)
                {
                    return Error(HttpStatusCode.PreconditionFailed, "SourceConditionNotMet");
                }

                var meta = headers.Where(static h => h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(static h => h.Key["x-ms-meta-".Length..], static h => h.Value, StringComparer.OrdinalIgnoreCase);
                var copied = new StoredBlob(source.Content, NextEtag(), meta);
                _blobs[path] = copied;
                var response = Etagged(HttpStatusCode.Accepted, copied);
                response.Headers.Add("x-ms-copy-id", Guid.NewGuid().ToString());
                response.Headers.Add("x-ms-copy-status", "success");
                return response;
            }

            if (method == HttpMethod.Get || method == HttpMethod.Head)
            {
                if (!_blobs.TryGetValue(path, out var blob))
                {
                    return Error(HttpStatusCode.NotFound, "BlobNotFound");
                }

                if (headers.TryGetValue("If-Match", out var expectedEtag) && expectedEtag != blob.ETag)
                {
                    return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                }

                var response = Etagged(HttpStatusCode.OK, blob);
                response.Content = method == HttpMethod.Get ? new ByteArrayContent(blob.Content) : new ByteArrayContent([]);
                response.Content.Headers.ContentLength = blob.Content.Length;
                response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
                response.Headers.Add("x-ms-blob-type", "BlockBlob");
                foreach (var meta in blob.Metadata)
                {
                    response.Headers.Add("x-ms-meta-" + meta.Key, meta.Value);
                }

                if (method == HttpMethod.Head)
                {
                    var afterHead = AfterHead;
                    AfterHead = null;
                    afterHead?.Invoke();
                }

                return response;
            }

            if (method == HttpMethod.Delete)
            {
                if (FailDeletes)
                {
                    return Error(HttpStatusCode.InternalServerError, "InternalError");
                }

                return _blobs.Remove(path) ? new HttpResponseMessage(HttpStatusCode.Accepted) : Error(HttpStatusCode.NotFound, "BlobNotFound");
            }

            return Error(HttpStatusCode.BadRequest, "UnsupportedFakeOperation");
        }

        string NextEtag() => $"\"0x{Interlocked.Increment(ref _etagCounter):X}\"";

        static HttpResponseMessage Etagged(HttpStatusCode status, StoredBlob blob)
        {
            var response = new HttpResponseMessage(status);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(blob.ETag);
            response.Headers.Add("x-ms-request-server-encrypted", "true");
            return response;
        }

        static HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            var response = Xml(status, new XElement("Error", new XElement("Code", code), new XElement("Message", code)));
            response.Headers.Add("x-ms-error-code", code);
            return response;
        }

        static HttpResponseMessage Xml(HttpStatusCode status, XElement root) => new(status)
        {
            Content = new StringContent(new XDocument(root).ToString(), Encoding.UTF8, "application/xml")
        };

        static HttpResponseMessage DelegationKey(byte[] body)
        {
            var info = XDocument.Parse(Encoding.UTF8.GetString(body).TrimStart('\uFEFF')).Root!;
            return Xml(HttpStatusCode.OK, new XElement("UserDelegationKey",
                new XElement("SignedOid", Guid.NewGuid()),
                new XElement("SignedTid", Guid.NewGuid()),
                new XElement("SignedStart", info.Element("Start")!.Value),
                new XElement("SignedExpiry", info.Element("Expiry")!.Value),
                new XElement("SignedService", "b"),
                new XElement("SignedVersion", "2020-02-10"),
                new XElement("Value", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))));
        }

        sealed record StoredBlob(byte[] Content, string ETag, Dictionary<string, string> Metadata)
        {
            public Dictionary<string, byte[]> Blocks { get; init; } = [with(StringComparer.Ordinal)];
        }
    }
}

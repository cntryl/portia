namespace Cntryl.Portia.Tests.Storage;

/// <summary>Behavioral checks for the object storage fake and shared contract rules.</summary>
public sealed class FakeObjectStorageTests
{
    /// <summary>Checks content verification and tenant isolation.</summary>
    [Fact]
    public async Task ShouldVerifyAndIsolateObjectsGivenMultipleTenants()
    {
        // Arrange
        var store = new FakeObjectStorage();
        var content = "immutable tenant content"u8.ToArray();
        var digest = Digest(content);
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        // Act
        await UploadAsync(store, tenantA, digest, content);

        // Assert
        Assert.True(await store.HeadAndVerifyAsync(tenantA, digest, TestContext.Current.CancellationToken));
        Assert.False(await store.HeadAndVerifyAsync(tenantB, digest, TestContext.Current.CancellationToken));
        var download = await store.CreateDownloadAsync(tenantA, digest, TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
        Assert.True(download.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(download.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(3));
        await using var read = await store.OpenReadAsync(tenantA, digest, TestContext.Current.CancellationToken);
        Assert.NotNull(read);
        using var copy = new MemoryStream();
        await read!.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(content, copy.ToArray());

        await UploadAsync(store, tenantB, digest, content);
        Assert.True(await store.HeadAndVerifyAsync(tenantA, digest, TestContext.Current.CancellationToken));
        Assert.True(await store.HeadAndVerifyAsync(tenantB, digest, TestContext.Current.CancellationToken));
    }

    /// <summary>Checks that mismatched uploads fail and discard their session.</summary>
    [Fact]
    public async Task ShouldRejectAndRemoveFailedSessionGivenMismatchedContent()
    {
        // Arrange
        var store = new FakeObjectStorage();
        var expected = Digest("expected"u8.ToArray());
        var session = await store.CreateUploadAsync(new TenantId("tenant-a"), expected, TestContext.Current.CancellationToken);

        // Act
        var receipt = await store.AcceptPartAsync(session, 1, new MemoryStream("actual"u8.ToArray()), TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CompleteUploadAsync(session, [receipt], TestContext.Current.CancellationToken));

        Assert.Equal(0, store.ActiveUploadCount);
        Assert.False(await store.HeadAndVerifyAsync(new TenantId("tenant-a"), expected, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Checks that a mistyped part receipt leaves the session open for a corrected completion,
    /// as S3 keeps the multipart upload open when it rejects a receipt.
    /// </summary>
    [Fact]
    public async Task ShouldKeepSessionOpenGivenUnknownPartReceipt()
    {
        // Arrange
        var store = new FakeObjectStorage();
        var tenant = new TenantId("tenant-a");
        var content = "browser upload"u8.ToArray();
        var digest = Digest(content);
        var session = await store.CreateUploadAsync(tenant, digest, TestContext.Current.CancellationToken);
        var receipt = await store.AcceptPartAsync(session, 1, new MemoryStream(content), TestContext.Current.CancellationToken);
        var rejected = await Record.ExceptionAsync(async () =>
            await store.CompleteUploadAsync(session, [receipt with { Token = "mistyped-etag" }], TestContext.Current.CancellationToken));

        // Act
        var corrected = await Record.ExceptionAsync(async () => await store.CompleteUploadAsync(session, [receipt], TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(rejected);
        Assert.Null(corrected);
        Assert.True(await store.HeadAndVerifyAsync(tenant, digest, TestContext.Current.CancellationToken));
    }

    /// <summary>Checks abort cleanup and bounded presigned lifetimes.</summary>
    [Fact]
    public async Task ShouldAbortSessionAndBoundSignedLifetimesGivenOpenSession()
    {
        // Arrange
        var store = new FakeObjectStorage();
        var expected = Digest("content"u8.ToArray());
        var session = await store.CreateUploadAsync(new TenantId("tenant-a"), expected, TestContext.Current.CancellationToken);

        // Act
        var part = await store.SignPartAsync(session, 1, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(part.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.SignPartAsync(session, 1, ObjectStorageLimits.MaximumDownloadLifetime + TimeSpan.FromTicks(1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.CreateDownloadAsync(new TenantId("tenant-a"), expected,
                ObjectStorageLimits.MaximumDownloadLifetime + TimeSpan.FromTicks(1), TestContext.Current.CancellationToken));

        await store.AbortUploadAsync(session, TestContext.Current.CancellationToken);
        Assert.Equal(0, store.ActiveUploadCount);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SignPartAsync(session, 1, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
    }

    /// <summary>Checks the maximum object size and digest validation.</summary>
    [Fact]
    public void ShouldEnforce256MiBBoundaryGivenMaximumAndOversizedDigests()
    {
        // Arrange
        var digest = new string('a', 64);

        // Act
        var maximum = new ObjectDigest(digest, ObjectStorageLimits.MaximumObjectLength);
        var tooLong = Record.Exception(() => new ObjectDigest(digest, ObjectStorageLimits.MaximumObjectLength + 1));
        var badDigest = Record.Exception(() => new ObjectDigest("not-a-digest", 0));

        // Assert
        Assert.Equal(ObjectStorageLimits.MaximumObjectLength, maximum.Length);
        _ = Assert.IsType<ArgumentOutOfRangeException>(tooLong);
        _ = Assert.IsType<ArgumentException>(badDigest);
    }

    /// <summary>Runs the reusable provider conformance suite against the fake.</summary>
    [Fact]
    public async Task ShouldPassReusableStorageConformanceGivenFakeStorage()
    {
        // Arrange
        var store = new FakeObjectStorage();

        // Act
        var exception = await Record.ExceptionAsync(async () => await ObjectStorageConformance.VerifyAsync(
            store,
            new TenantId("conformance-a"),
            new TenantId("conformance-b"),
            async (part, content, ct) => await store.AcceptPartAsync(
                new ObjectUploadSession(part.UploadUri.AbsolutePath.Split('/')[1]),
                part.PartNumber,
                new MemoryStream(content.ToArray()),
                ct), TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Matches S3's minimum size for every part except the final one, keeping the session open for corrected parts.</summary>
    [Fact]
    public async Task ShouldRejectPartGivenUndersizedNonfinalPart()
    {
        // Arrange
        var store = new FakeObjectStorage();
        var content = new byte[8 * 1024 * 1024];
        var session = await store.CreateUploadAsync(new TenantId("tenant-a"), Digest(content), TestContext.Current.CancellationToken);
        var first = await store.AcceptPartAsync(session, 1, new MemoryStream(content, 0, 4 * 1024 * 1024), TestContext.Current.CancellationToken);
        var last = await store.AcceptPartAsync(session, 2, new MemoryStream(content, 4 * 1024 * 1024, 4 * 1024 * 1024), TestContext.Current.CancellationToken);

        // Act
        var error = await Record.ExceptionAsync(async () => await store.CompleteUploadAsync(session, [first, last], TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(error);
        Assert.Equal(1, store.ActiveUploadCount);
    }

    /// <summary>Checks concurrent singleton access to uploads, parts, and tenant objects.</summary>
    [Fact]
    public async Task ShouldCompleteUploadsGivenParallelUploadsAndParts()
    {
        // Arrange
        var store = new FakeObjectStorage();
        var tenant = new TenantId("tenant-a");
        var content = new byte[ObjectStorageLimits.MinimumNonfinalPartLength * 2 + 1];
        var digest = Digest(content);
        var session = await store.CreateUploadAsync(tenant, digest, TestContext.Current.CancellationToken);
        var parts = await Task.WhenAll(Enumerable.Range(1, 3).Select(partNumber => Task.Run(async () =>
        {
            var start = (partNumber - 1) * ObjectStorageLimits.MinimumNonfinalPartLength;
            var length = partNumber == 3 ? 1 : ObjectStorageLimits.MinimumNonfinalPartLength;
            return await store.AcceptPartAsync(session, partNumber, new MemoryStream(content, start, length));
        })));

        // Act
        await store.CompleteUploadAsync(session, parts, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(await store.HeadAndVerifyAsync(tenant, digest, TestContext.Current.CancellationToken));

        var payloads = Enumerable.Range(0, 64).Select(index => BitConverter.GetBytes(index)).ToArray();
        await Task.WhenAll(payloads.Select(payload => Task.Run(async () =>
            await UploadAsync(store, tenant, Digest(payload), payload))));

        Assert.Equal(0, store.ActiveUploadCount);

        foreach (var payload in payloads)
        {
            Assert.True(await store.HeadAndVerifyAsync(tenant, Digest(payload), TestContext.Current.CancellationToken));
        }
    }

    /// <summary>An in-flight part cannot repopulate a session after abort.</summary>
    [Fact]
    public async Task ShouldRejectPartGivenPartFinishingAfterAbort()
    {
        // Arrange
        var store = new FakeObjectStorage();
        var session = await store.CreateUploadAsync(new TenantId("tenant-a"), Digest([1]), TestContext.Current.CancellationToken);
        using var stream = new PausingStream([1]);
        var acceptance = store.AcceptPartAsync(session, 1, stream, TestContext.Current.CancellationToken).AsTask();

        await stream.ReadStarted;
        await store.AbortUploadAsync(session, TestContext.Current.CancellationToken);

        // Act
        stream.Resume();

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => acceptance);
        Assert.Equal(0, store.ActiveUploadCount);
    }

    /// <summary>Only the same completed session and receipts can replay a verified promotion.</summary>
    [Fact]
    public async Task ShouldRetrySuccessfulCompletionWithoutConfusionGivenCrossTenantReceipts()
    {
        // Arrange
        var store = new FakeObjectStorage();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        var content = "retryable content"u8.ToArray();
        var digest = Digest(content);
        var session = await store.CreateUploadAsync(tenantA, digest, TestContext.Current.CancellationToken);
        var receipt = await store.AcceptPartAsync(session, 1, new MemoryStream(content), TestContext.Current.CancellationToken);

        await store.CompleteUploadAsync(session, [receipt], TestContext.Current.CancellationToken);

        // Act
        await store.CompleteUploadAsync(session, [new ObjectPartReceipt(receipt.PartNumber, receipt.Token)], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, store.ActiveUploadCount);
        Assert.True(await store.HeadAndVerifyAsync(tenantA, digest, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CompleteUploadAsync(session, [new ObjectPartReceipt(receipt.PartNumber, "altered-receipt")], TestContext.Current.CancellationToken));

        await UploadAsync(store, tenantB, digest, content);
        await store.DeleteAsync(tenantA, digest, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await store.CompleteUploadAsync(session, [receipt], TestContext.Current.CancellationToken));
        Assert.True(await store.HeadAndVerifyAsync(tenantB, digest, TestContext.Current.CancellationToken));
    }

    static ObjectDigest Digest(byte[] content)
        => new(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);

    static async Task UploadAsync(FakeObjectStorage store, TenantId tenant, ObjectDigest digest, byte[] content)
    {
        var session = await store.CreateUploadAsync(tenant, digest);
        var part = await store.SignPartAsync(session, 1, TimeSpan.FromMinutes(1));
        var receipt = await store.AcceptPartAsync(session, part.PartNumber, new MemoryStream(content));
        await store.CompleteUploadAsync(session, [receipt]);
    }

    sealed class PausingStream(byte[] content) : MemoryStream(content)
    {
        readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;

        public void Resume() => _resume.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            _readStarted.TrySetResult();
            await _resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}

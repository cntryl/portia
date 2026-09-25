namespace Cntryl.Portia.Tests.Storage;

/// <summary>Behavioral checks for the object storage fake and shared contract rules.</summary>
public sealed class FakeObjectStorageTests
{
    /// <summary>Checks content verification and tenant isolation.</summary>
    [Fact]
    public async Task ShouldVerifyAndIsolateObjectsByTenant()
    {
        var store = new FakeObjectStorage();
        var content = "immutable tenant content"u8.ToArray();
        var digest = Digest(content);
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        await UploadAsync(store, tenantA, digest, content);

        Assert.True(await store.HeadAndVerifyAsync(tenantA, digest));
        Assert.False(await store.HeadAndVerifyAsync(tenantB, digest));
        var download = await store.CreateDownloadAsync(tenantA, digest, TimeSpan.FromMinutes(3));
        Assert.True(download.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(download.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(3));
        await using var read = await store.OpenReadAsync(tenantA, digest);
        Assert.NotNull(read);
        using var copy = new MemoryStream();
        await read!.CopyToAsync(copy);
        Assert.Equal(content, copy.ToArray());

        await UploadAsync(store, tenantB, digest, content);
        Assert.True(await store.HeadAndVerifyAsync(tenantA, digest));
        Assert.True(await store.HeadAndVerifyAsync(tenantB, digest));
    }

    /// <summary>Checks that mismatched uploads fail and discard their session.</summary>
    [Fact]
    public async Task ShouldRejectMismatchedContentAndRemoveFailedSession()
    {
        var store = new FakeObjectStorage();
        var expected = Digest("expected"u8.ToArray());
        var session = await store.CreateUploadAsync(new TenantId("tenant-a"), expected);
        var receipt = await store.AcceptPartAsync(session, 1, new MemoryStream("actual"u8.ToArray()));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CompleteUploadAsync(session, [receipt]));

        Assert.Equal(0, store.ActiveUploadCount);
        Assert.False(await store.HeadAndVerifyAsync(new TenantId("tenant-a"), expected));
    }

    /// <summary>Checks abort cleanup and bounded presigned lifetimes.</summary>
    [Fact]
    public async Task ShouldAbortSessionAndBoundSignedLifetimes()
    {
        var store = new FakeObjectStorage();
        var expected = Digest("content"u8.ToArray());
        var session = await store.CreateUploadAsync(new TenantId("tenant-a"), expected);
        var part = await store.SignPartAsync(session, 1, TimeSpan.FromMinutes(2));
        Assert.True(part.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.SignPartAsync(session, 1, ObjectStorageLimits.MaximumDownloadLifetime + TimeSpan.FromTicks(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.CreateDownloadAsync(new TenantId("tenant-a"), expected, ObjectStorageLimits.MaximumDownloadLifetime + TimeSpan.FromTicks(1)));

        await store.AbortUploadAsync(session);
        Assert.Equal(0, store.ActiveUploadCount);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SignPartAsync(session, 1, TimeSpan.FromMinutes(1)));
    }

    /// <summary>Checks the maximum object size and digest validation.</summary>
    [Fact]
    public void ShouldEnforceThe256MiBBoundary()
    {
        var digest = new string('a', 64);
        Assert.Equal(ObjectStorageLimits.MaximumObjectLength, new ObjectDigest(digest, ObjectStorageLimits.MaximumObjectLength).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectDigest(digest, ObjectStorageLimits.MaximumObjectLength + 1));
        Assert.Throws<ArgumentException>(() => new ObjectDigest("not-a-digest", 0));
    }

    /// <summary>Runs the reusable provider conformance suite against the fake.</summary>
    [Fact]
    public async Task ShouldPassReusableStorageConformance()
    {
        var store = new FakeObjectStorage();
        await ObjectStorageConformance.VerifyAsync(
            store,
            new TenantId("conformance-a"),
            new TenantId("conformance-b"),
            async (part, content, ct) => await store.AcceptPartAsync(
                new ObjectUploadSession(part.UploadUri.AbsolutePath.Split('/')[1]),
                part.PartNumber,
                new MemoryStream(content.ToArray()),
                ct));
    }

    /// <summary>Matches S3's minimum size for every part except the final one.</summary>
    [Fact]
    public async Task ShouldRejectAnUndersizedNonfinalPart()
    {
        var store = new FakeObjectStorage();
        var content = new byte[8 * 1024 * 1024];
        var session = await store.CreateUploadAsync(new TenantId("tenant-a"), Digest(content));
        var first = await store.AcceptPartAsync(session, 1, new MemoryStream(content, 0, 4 * 1024 * 1024));
        var last = await store.AcceptPartAsync(session, 2, new MemoryStream(content, 4 * 1024 * 1024, 4 * 1024 * 1024));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CompleteUploadAsync(session, [first, last]));

        Assert.Equal(0, store.ActiveUploadCount);
    }

    /// <summary>Checks concurrent singleton access to uploads, parts, and tenant objects.</summary>
    [Fact]
    public async Task ShouldCompleteParallelUploadsAndParts()
    {
        var store = new FakeObjectStorage();
        var tenant = new TenantId("tenant-a");
        var content = new byte[ObjectStorageLimits.MinimumNonfinalPartLength * 2 + 1];
        var digest = Digest(content);
        var session = await store.CreateUploadAsync(tenant, digest);
        var parts = await Task.WhenAll(Enumerable.Range(1, 3).Select(partNumber => Task.Run(async () =>
        {
            var start = (partNumber - 1) * ObjectStorageLimits.MinimumNonfinalPartLength;
            var length = partNumber == 3 ? 1 : ObjectStorageLimits.MinimumNonfinalPartLength;
            return await store.AcceptPartAsync(session, partNumber, new MemoryStream(content, start, length));
        })));

        await store.CompleteUploadAsync(session, parts);
        Assert.True(await store.HeadAndVerifyAsync(tenant, digest));

        var payloads = Enumerable.Range(0, 64).Select(index => BitConverter.GetBytes(index)).ToArray();
        await Task.WhenAll(payloads.Select(payload => Task.Run(async () =>
            await UploadAsync(store, tenant, Digest(payload), payload))));

        Assert.Equal(0, store.ActiveUploadCount);
        foreach (var payload in payloads)
        {
            Assert.True(await store.HeadAndVerifyAsync(tenant, Digest(payload)));
        }
    }

    /// <summary>An in-flight part cannot repopulate a session after abort.</summary>
    [Fact]
    public async Task ShouldRejectPartThatFinishesAfterAbort()
    {
        var store = new FakeObjectStorage();
        var session = await store.CreateUploadAsync(new TenantId("tenant-a"), Digest([1]));
        using var stream = new PausingStream([1]);
        var acceptance = store.AcceptPartAsync(session, 1, stream).AsTask();

        await stream.ReadStarted;
        await store.AbortUploadAsync(session);
        stream.Resume();

        await Assert.ThrowsAsync<InvalidOperationException>(() => acceptance);
        Assert.Equal(0, store.ActiveUploadCount);
    }

    /// <summary>Only the same completed session and receipts can replay a verified promotion.</summary>
    [Fact]
    public async Task ShouldRetrySuccessfulCompletionWithoutCrossTenantOrReceiptConfusion()
    {
        var store = new FakeObjectStorage();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        var content = "retryable content"u8.ToArray();
        var digest = Digest(content);
        var session = await store.CreateUploadAsync(tenantA, digest);
        var receipt = await store.AcceptPartAsync(session, 1, new MemoryStream(content));

        await store.CompleteUploadAsync(session, [receipt]);
        await store.CompleteUploadAsync(session, [new ObjectPartReceipt(receipt.PartNumber, receipt.Token)]);
        Assert.Equal(0, store.ActiveUploadCount);
        Assert.True(await store.HeadAndVerifyAsync(tenantA, digest));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CompleteUploadAsync(session, [new ObjectPartReceipt(receipt.PartNumber, "altered-receipt")]));

        await UploadAsync(store, tenantB, digest, content);
        await store.DeleteAsync(tenantA, digest);
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await store.CompleteUploadAsync(session, [receipt]));
        Assert.True(await store.HeadAndVerifyAsync(tenantB, digest));
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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            await _resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}

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

    static ObjectDigest Digest(byte[] content)
        => new(Convert.ToHexString(SHA256.HashData(content)), content.LongLength);

    static async Task UploadAsync(FakeObjectStorage store, TenantId tenant, ObjectDigest digest, byte[] content)
    {
        var session = await store.CreateUploadAsync(tenant, digest);
        var part = await store.SignPartAsync(session, 1, TimeSpan.FromMinutes(1));
        var receipt = await store.AcceptPartAsync(session, part.PartNumber, new MemoryStream(content));
        await store.CompleteUploadAsync(session, [receipt]);
    }
}

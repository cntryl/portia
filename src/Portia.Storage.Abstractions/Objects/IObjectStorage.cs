namespace Cntryl.Portia.Storage;

/// <summary>Stores immutable tenant-scoped objects and provides bounded delivery links.</summary>
public interface IObjectStorage
{
    /// <summary>Starts a multipart upload for the content identified by its digest.</summary>
    ValueTask<ObjectUploadSession> CreateUploadAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default);

    /// <summary>Creates a short-lived URL for one part of an active upload.</summary>
    ValueTask<ObjectUploadPart> SignPartAsync(ObjectUploadSession session, int partNumber, TimeSpan lifetime, CancellationToken ct = default);

    /// <summary>Completes, verifies, and promotes the upload to its immutable content key. Every nonfinal part must be at least <see cref="ObjectStorageLimits.MinimumNonfinalPartLength"/> bytes.</summary>
    ValueTask CompleteUploadAsync(ObjectUploadSession session, IReadOnlyList<ObjectPartReceipt> parts, CancellationToken ct = default);

    /// <summary>Aborts an active multipart upload and removes any temporary data.</summary>
    ValueTask AbortUploadAsync(ObjectUploadSession session, CancellationToken ct = default);

    /// <summary>Checks the promoted object length and adapter-verified digest metadata.</summary>
    ValueTask<bool> HeadAndVerifyAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default);

    /// <summary>Creates a short-lived download URL for an existing verified object.</summary>
    ValueTask<ObjectDownload> CreateDownloadAsync(TenantId tenantId, ObjectDigest expected, TimeSpan lifetime, CancellationToken ct = default);

    /// <summary>Opens a stream for an existing verified object.</summary>
    ValueTask<Stream?> OpenReadAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default);

    /// <summary>Deletes the object for this tenant and digest.</summary>
    ValueTask DeleteAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default);
}

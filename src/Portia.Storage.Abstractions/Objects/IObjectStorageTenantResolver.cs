namespace Cntryl.Portia.Storage;

/// <summary>Resolves application-owned storage locations and object keys.</summary>
public interface IObjectStorageTenantResolver
{
    /// <summary>Resolves storage location and encryption references for a tenant.</summary>
    ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default);

    /// <summary>Gets the stable object key for this tenant and content. The application owns its layout and tenant isolation.</summary>
    /// <param name="tenantId">The tenant that owns the object.</param>
    /// <param name="expected">The expected content digest and length.</param>
    /// <returns>The nonblank, provider-valid object key; no prefix or digest layout is imposed.</returns>
    string GetObjectKey(TenantId tenantId, ObjectDigest expected);

    /// <summary>Gets a unique temporary key for this upload. Providers that stage separate parts store them beneath this key.</summary>
    /// <param name="tenantId">The tenant that owns the upload.</param>
    /// <param name="uploadId">A new identity supplied by the adapter for this upload.</param>
    /// <returns>A nonblank, provider-valid key reserved for this upload and disjoint from final object keys.</returns>
    string GetUploadKey(TenantId tenantId, Uuid uploadId);
}

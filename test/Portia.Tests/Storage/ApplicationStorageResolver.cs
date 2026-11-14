namespace Cntryl.Portia.Tests.Storage;

/// <summary>An application layout that isolates tenants by key within a shared bucket.</summary>
sealed class ApplicationStorageResolver(string encryptionKey, string bucket = ApplicationStorageResolver.Bucket) : IObjectStorageTenantResolver
{
    internal const string Bucket = "shared-assets";
    internal string ContentRoot { get; set; } = "documents";
    internal string UploadRoot { get; set; } = "pending";

    public ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
        ValueTask.FromResult(new ObjectStorageLocation(bucket, encryptionKey));

    public string GetObjectKey(TenantId tenantId, ObjectDigest expected) =>
        $"{ContentRoot}/{tenantId.Value}/{expected.Sha256}";

    public string GetUploadKey(TenantId tenantId, Uuid uploadId) =>
        $"{UploadRoot}/{tenantId.Value}/{uploadId:N}";
}

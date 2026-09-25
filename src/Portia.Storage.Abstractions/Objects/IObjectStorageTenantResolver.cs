namespace Cntryl.Portia.Storage;

/// <summary>Resolves a Portia tenant to opaque provider configuration references.</summary>
public interface IObjectStorageTenantResolver
{
    /// <summary>Resolves storage location and encryption references for a tenant.</summary>
    ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default);
}

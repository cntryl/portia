using Cntryl.Portia;
using Cntryl.Portia.Storage;

namespace StorageSmoke.Contracts;

/// <summary>An application's tenant-scoped reference to immutable object content.</summary>
public sealed record AssetReference(TenantId Tenant, ObjectDigest Content);

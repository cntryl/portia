namespace Cntryl.Portia.Storage;

// The parts of the object-storage contract that do not depend on a vendor. Every provider enforces them
// through this one implementation, so a rule cannot hold for one store and drift in another.
static class ObjectStorageRules
{
    internal static TimeSpan BoundLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > ObjectStorageLimits.MaximumDownloadLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime),
                $"Lifetime must be positive and no longer than {ObjectStorageLimits.MaximumDownloadLifetime}.");
        }

        return lifetime;
    }

    internal static void EnsureSessionActive(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (expiresAt <= now)
        {
            throw new InvalidOperationException("The upload session has expired.");
        }
    }

    internal static string ObjectKey(IObjectStorageTenantResolver resolver, TenantId tenantId, ObjectDigest expected)
    {
        var key = resolver.GetObjectKey(tenantId, expected);
        return !string.IsNullOrWhiteSpace(key)
            ? key
            : throw new InvalidOperationException("The application must resolve a nonblank object key.");
    }

    internal static string UploadKey(IObjectStorageTenantResolver resolver, TenantId tenantId, string objectKey)
    {
        var key = resolver.GetUploadKey(tenantId, Uuid.CreateVersion4());

        if (string.IsNullOrWhiteSpace(key) || string.Equals(key, objectKey, StringComparison.Ordinal) ||
            objectKey.StartsWith(key.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The application must resolve a unique upload key disjoint from the object key.");

        return key;
    }
}

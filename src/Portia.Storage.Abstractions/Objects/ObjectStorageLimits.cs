namespace Cntryl.Portia.Storage;

/// <summary>Limits shared by object storage providers.</summary>
public static class ObjectStorageLimits
{
    /// <summary>The maximum accepted object size, 256 MiB.</summary>
    public const long MaximumObjectLength = 256L * 1024 * 1024;

    /// <summary>The minimum size of every multipart part except the final part, 5 MiB.</summary>
    public const int MinimumNonfinalPartLength = 5 * 1024 * 1024;

    /// <summary>The longest permitted signed download lifetime.</summary>
    public static readonly TimeSpan MaximumDownloadLifetime = TimeSpan.FromMinutes(15);
}

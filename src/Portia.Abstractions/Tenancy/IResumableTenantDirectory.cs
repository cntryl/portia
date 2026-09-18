namespace Cntryl.Portia;

/// <summary>
///     An optional tenant-directory capability that gives each caller independent, process-lifetime
///     progress across interrupted reads.
/// </summary>
public interface IResumableTenantDirectory : ITenantDirectory
{
    /// <summary>Opens an independently progressing cursor.</summary>
    /// <param name="ct">A token that can cancel cursor creation.</param>
    /// <returns>A cursor owned by the caller.</returns>
    ValueTask<ITenantDirectoryCursor> OpenCursorAsync(CancellationToken ct = default);
}

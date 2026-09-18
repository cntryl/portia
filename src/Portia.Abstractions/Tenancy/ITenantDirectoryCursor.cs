namespace Cntryl.Portia;

/// <summary>
///     Reads initial active membership followed by ordered tenant changes. Re-enumerating the same
///     cursor resumes from its in-process offset; progress is not durable across process restarts.
/// </summary>
public interface ITenantDirectoryCursor : IAsyncDisposable
{
    /// <summary>
    ///     Reads from the cursor's current position. Only one enumeration may be active at a time.
    /// </summary>
    /// <param name="ct">A token that can cancel the current enumeration.</param>
    /// <returns>Initial additions and subsequent ordered changes.</returns>
    IAsyncEnumerable<TenantLifecycleChange> ReadAsync(CancellationToken ct = default);
}

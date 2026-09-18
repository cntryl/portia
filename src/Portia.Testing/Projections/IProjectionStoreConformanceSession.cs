namespace Cntryl.Portia.Testing;

/// <summary>
///     Represents one independently scoped application repository used by projection conformance tests.
///     Application writes staged through this session must participate in batches begun through
///     <see cref="Store" />.
/// </summary>
public interface IProjectionStoreConformanceSession : IAsyncDisposable
{
    /// <summary>Gets the projection store implemented by this repository session.</summary>
    IProjectionStore Store { get; }

    /// <summary>Stages the suite's application value in the currently active batch.</summary>
    /// <param name="value">The application value to stage.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the value is staged in the batch.</returns>
    ValueTask StageValueAsync(string value, CancellationToken ct = default);

    /// <summary>Reads the committed application value for an identity, outside an active batch.</summary>
    /// <param name="identity">The projection whose committed value is read.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The committed value, or <see langword="null" /> when nothing has been committed.</returns>
    ValueTask<string?> ReadValueAsync(CheckpointIdentity identity, CancellationToken ct = default);

    /// <summary>Injects one failure at the next commit before either data or progress becomes visible.</summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the failure is armed.</returns>
    ValueTask FailNextCommitAsync(CancellationToken ct = default);
}

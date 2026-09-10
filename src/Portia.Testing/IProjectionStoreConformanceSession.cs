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
    ValueTask StageValueAsync(string value, CancellationToken ct = default);

    /// <summary>Reads the committed application value for an identity, outside an active batch.</summary>
    ValueTask<string?> ReadValueAsync(CheckpointIdentity identity, CancellationToken ct = default);

    /// <summary>Injects one failure at the next commit before either data or progress becomes visible.</summary>
    ValueTask FailNextCommitAsync(CancellationToken ct = default);
}

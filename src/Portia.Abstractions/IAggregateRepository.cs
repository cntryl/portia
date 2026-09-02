namespace Cntryl.Portia;

/// <summary>
/// Loads and saves aggregates through their event histories.
/// </summary>
public interface IAggregateRepository
{
    /// <summary>
    /// Loads and rehydrates an aggregate by identity.
    /// </summary>
    /// <param name="id">The aggregate identity.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The rehydrated aggregate, or <see langword="null" /> when its event stream does not exist.</returns>
    ValueTask<TAggregate?> LoadAsync<TAggregate>(Uuid id, CancellationToken ct = default)
        where TAggregate : Aggregate;

    /// <summary>
    /// Persists an aggregate's uncommitted state-changing events.
    /// </summary>
    /// <param name="aggregate">The aggregate to save.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the save operation.</returns>
    ValueTask SaveAsync<TAggregate>(TAggregate aggregate, CancellationToken ct = default)
        where TAggregate : Aggregate;
}

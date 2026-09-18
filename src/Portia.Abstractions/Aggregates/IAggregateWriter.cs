namespace Cntryl.Portia;

/// <summary>Persists an aggregate's pending raised events or audits.</summary>
public interface IAggregateWriter
{
    /// <summary>Stamps pending raised events or audits with execution attribution, frozen across save retries.</summary>
    /// <typeparam name="TAggregate">The concrete aggregate type.</typeparam>
    /// <param name="aggregate">The aggregate whose pending events are appended.</param>
    /// <param name="context">The execution that attributes the appended events.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the events are committed.</returns>
    /// <exception cref="EventStreamConcurrencyException">The stream moved on since the aggregate was hydrated.</exception>
    ValueTask SaveAsync<TAggregate>(TAggregate aggregate, IExecutionContext context, CancellationToken ct = default)
        where TAggregate : Aggregate;
}

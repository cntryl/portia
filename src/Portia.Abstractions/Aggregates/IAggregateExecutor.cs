namespace Cntryl.Portia;

/// <summary>
///     Executes one explicit operation against one hydrated aggregate. Aggregate methods decide what happened;
///     the operation decides the result and makes one commit-or-discard decision for everything it produced;
///     the executor hydrates, invokes, and carries out that decision atomically. It never selects the
///     aggregate, chooses the domain method, interprets the result, retries a conflict, or coordinates other
///     aggregates. Two durability boundaries are two executions.
/// </summary>
public interface IAggregateExecutor
{
    /// <summary>Hydrates the aggregate, runs the operation, and commits or discards what it produced.</summary>
    /// <typeparam name="TAggregate">The concrete aggregate type.</typeparam>
    /// <param name="aggregate">The caller-constructed aggregate.</param>
    /// <param name="operation">Invokes aggregate methods and returns the result and disposition.</param>
    /// <param name="context">The execution that attributes committed records.</param>
    /// <param name="ct">A token that can cancel hydration or the commit.</param>
    /// <returns>The operation's result.</returns>
    /// <exception cref="EventStreamConcurrencyException">
    ///     The stream position changed or another append session is active. Reload and re-evaluate the
    ///     operation; Portia does not retry it automatically.
    /// </exception>
    ValueTask<Result> ExecuteAsync<TAggregate>(TAggregate aggregate, Func<TAggregate, AggregateOutcome> operation,
        IExecutionContext context, CancellationToken ct = default)
        where TAggregate : Aggregate;

    /// <summary>Hydrates the aggregate, runs the operation, and commits or discards what it produced.</summary>
    /// <typeparam name="TAggregate">The concrete aggregate type.</typeparam>
    /// <typeparam name="TOut">The type of the value on success.</typeparam>
    /// <param name="aggregate">The caller-constructed aggregate.</param>
    /// <param name="operation">Invokes aggregate methods and returns the result and disposition.</param>
    /// <param name="context">The execution that attributes committed records.</param>
    /// <param name="ct">A token that can cancel hydration or the commit.</param>
    /// <returns>The operation's result.</returns>
    /// <exception cref="EventStreamConcurrencyException">
    ///     The stream position changed or another append session is active. Reload and re-evaluate the
    ///     operation; Portia does not retry it automatically.
    /// </exception>
    ValueTask<Result<TOut>> ExecuteAsync<TAggregate, TOut>(TAggregate aggregate,
        Func<TAggregate, AggregateOutcome<TOut>> operation, IExecutionContext context, CancellationToken ct = default)
        where TAggregate : Aggregate;
}

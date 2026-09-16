namespace Cntryl.Portia;

/// <summary>Executes one operation against one hydrated aggregate and carries out its commit-or-discard decision.</summary>
/// <param name="reader">Hydrates the aggregate.</param>
/// <param name="writer">Commits what the operation produced.</param>
public sealed class AggregateExecutor(IAggregateReader reader, IAggregateWriter writer) : IAggregateExecutor
{
    /// <inheritdoc />
    public async ValueTask<Result> ExecuteAsync<TAggregate>(TAggregate aggregate,
        Func<TAggregate, AggregateOutcome> operation, IExecutionContext context, CancellationToken ct = default)
        where TAggregate : Aggregate
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        var hydrated = await reader.HydrateAsync(aggregate, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var outcome = operation(hydrated);
        await ApplyAsync(hydrated, outcome.Disposition, context, ct).ConfigureAwait(false);
        return outcome.Result;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TOut>> ExecuteAsync<TAggregate, TOut>(TAggregate aggregate,
        Func<TAggregate, AggregateOutcome<TOut>> operation, IExecutionContext context, CancellationToken ct = default)
        where TAggregate : Aggregate
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        var hydrated = await reader.HydrateAsync(aggregate, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var outcome = operation(hydrated);
        await ApplyAsync(hydrated, outcome.Disposition, context, ct).ConfigureAwait(false);
        return outcome.Result;
    }

    ValueTask ApplyAsync<TAggregate>(TAggregate aggregate, AggregateDisposition disposition,
        IExecutionContext context, CancellationToken ct)
        where TAggregate : Aggregate
    {
        switch (disposition)
        {
            case AggregateDisposition.Commit:
                return writer.SaveAsync(aggregate, context, ct);
            case AggregateDisposition.Discard:
                aggregate.DiscardPending();
                return ValueTask.CompletedTask;
            default:
                aggregate.DiscardPending();
                throw new InvalidOperationException(
                    $"An operation on aggregate '{typeof(TAggregate).FullName}' returned an uninitialized {nameof(AggregateOutcome)}; return AggregateOutcome.Commit or AggregateOutcome.Discard.");
        }
    }
}

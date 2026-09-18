namespace Cntryl.Portia.Testing;

/// <summary>The scope's aggregate read, write, and execute capabilities, for tests that exercise persistence directly.</summary>
public sealed class AggregateCapabilities(IAggregateReader reader, IAggregateWriter writer, IAggregateExecutor executor)
    : IAggregateReader, IAggregateWriter, IAggregateExecutor
{
    /// <inheritdoc />
    public ValueTask<TAggregate> HydrateAsync<TAggregate>(TAggregate aggregate, CancellationToken ct = default)
        where TAggregate : Aggregate => reader.HydrateAsync(aggregate, ct);

    /// <inheritdoc />
    public ValueTask SaveAsync<TAggregate>(TAggregate aggregate, IExecutionContext context,
        CancellationToken ct = default)
        where TAggregate : Aggregate => writer.SaveAsync(aggregate, context, ct);

    // RS0026 flags these as ambiguous multi-overload optional parameters, but this exact pair is
    // IAggregateExecutor's own already-shipped shape (see AggregateExecutor in Portia.Core) — the
    // analyzer just doesn't re-check API that shipped before this rule applied to it.
#pragma warning disable RS0026
    /// <inheritdoc />
    public ValueTask<Result> ExecuteAsync<TAggregate>(TAggregate aggregate,
        Func<TAggregate, AggregateOutcome> operation,
        IExecutionContext context, CancellationToken ct = default)
        where TAggregate : Aggregate => executor.ExecuteAsync(aggregate, operation, context, ct);

    /// <inheritdoc />
    public ValueTask<Result<TOut>> ExecuteAsync<TAggregate, TOut>(TAggregate aggregate,
        Func<TAggregate, AggregateOutcome<TOut>> operation, IExecutionContext context, CancellationToken ct = default)
        where TAggregate : Aggregate => executor.ExecuteAsync(aggregate, operation, context, ct);
#pragma warning restore RS0026
}

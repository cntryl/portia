namespace Cntryl.Portia;

sealed class RecordingAggregateRepository : IAggregateRepository
{
    public List<TestAggregate> SavedAggregates { get; } = [];

    public ValueTask<TAggregate> HydrateAsync<TAggregate>(TAggregate aggregate, CancellationToken ct = default)
        where TAggregate : Aggregate
        => ValueTask.FromResult(aggregate);

    public ValueTask SaveAsync<TAggregate>(TAggregate aggregate, IExecutionContext context,
        CancellationToken ct = default)
        where TAggregate : Aggregate
    {
        if (aggregate is TestAggregate testAggregate)
        {
            SavedAggregates.Add(testAggregate);
        }

        return ValueTask.CompletedTask;
    }
}

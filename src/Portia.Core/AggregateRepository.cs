namespace Cntryl.Portia;

/// <summary>Persists aggregates through an event store and explicitly registered factories.</summary>
/// <param name="store">The aggregate event store.</param>
/// <param name="services">The current application scope.</param>
public sealed class AggregateRepository(IEventStore store, IServiceProvider services) : IAggregateRepository
{
    /// <inheritdoc />
    public async ValueTask<TAggregate?> LoadAsync<TAggregate>(Uuid id, CancellationToken ct = default)
        where TAggregate : Aggregate
    {
        var factory = services.GetService(typeof(AggregateFactory<TAggregate>)) as AggregateFactory<TAggregate>
            ?? throw new InvalidOperationException($"Register an aggregate factory for '{typeof(TAggregate)}' using AddPortiaAggregate.");
        var aggregate = factory(services, id);
        if (aggregate.Id != id)
            throw new InvalidOperationException("The aggregate factory returned a different identity.");

        var events = new List<DomainEvent>();
        var eventIds = new HashSet<Uuid>();
        var position = 0UL;
        var version = 0UL;
        await foreach (var record in store.ReadAsync(aggregate.Stream, ct: ct).ConfigureAwait(false))
        {
            var ev = record.Ev;
            DomainEventValidation.Validate(ev);
            if (record.Stream != aggregate.Stream || record.ResourceOffset != position)
                throw new InvalidOperationException("Aggregate records must have contiguous physical stream offsets.");
            if (ev.Metadata.AggregateId != id || !eventIds.Add(ev.Metadata.EventId))
                throw new InvalidOperationException("Aggregate records must have matching aggregate identity and unique event identities.");
            if (ev.Metadata.IsAudit)
                throw new InvalidOperationException("An aggregate's source stream cannot contain audits; persist audits in session streams.");
            version = checked(version + 1);
            if (ev.Metadata.AggregateVersion != version)
                throw new InvalidOperationException("Aggregate state versions must advance only for raised events.");
            events.Add(ev);
            position = checked(position + 1);
        }

        if (position == 0)
            return null;

        aggregate.Load([.. events], position);
        return aggregate;
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync<TAggregate>(TAggregate aggregate, CancellationToken ct = default)
        where TAggregate : Aggregate
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        using var operation = aggregate.BeginOperation();
        ct.ThrowIfCancellationRequested();
        if (aggregate.UncommittedEvents.Count != 0 && aggregate.UncommittedAudits.Count != 0)
            throw new InvalidOperationException("An aggregate save cannot contain both raised events and audits.");

        if (aggregate.UncommittedEvents.Count == 0 && aggregate.UncommittedAudits.Count == 0)
            return;

        var isAudit = aggregate.UncommittedAudits.Count != 0;
        DomainEvent[] pending = isAudit ? [.. aggregate.UncommittedAudits] : [.. aggregate.UncommittedEvents];
        DomainEventValidation.ValidateBatch(pending);
        var stream = isAudit ? aggregate.GetAuditSessionStream() : aggregate.Stream;
        var expectedPosition = isAudit ? 0 : aggregate.CommittedStreamPosition;
        await store.AppendAsync(stream, expectedPosition, pending, ct).ConfigureAwait(false);
        aggregate.Save();
    }
}

namespace Cntryl.Portia;

/// <summary>Hydrates caller-constructed aggregates and persists their events.</summary>
/// <param name="store">The aggregate event store.</param>
public sealed class AggregateRepository(IEventStore store) : IAggregateRepository
{
    /// <inheritdoc />
    public async ValueTask<TAggregate> HydrateAsync<TAggregate>(TAggregate aggregate, CancellationToken ct = default)
        where TAggregate : Aggregate
    {
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "fault";
        var eventCount = 0;
        try
        {
            ArgumentNullException.ThrowIfNull(aggregate);
            using var operation = aggregate.BeginOperation();
            ct.ThrowIfCancellationRequested();
            if (aggregate.UncommittedEvents.Count != 0 || aggregate.UncommittedAudits.Count != 0)
            {
                throw new InvalidOperationException("Save pending aggregate changes before hydration.");
            }

            var events = new List<DomainEvent>();
            var position = aggregate.CommittedStreamPosition;
            await foreach (var record in store.ReadAsync(aggregate.Stream, position, ct).ConfigureAwait(false))
            {
                var ev = record.Event;
                DomainEventValidation.Validate(ev);
                if (record.Stream != aggregate.Stream || record.ResourceOffset != position)
                {
                    throw new InvalidOperationException(
                        "Aggregate records must have contiguous physical stream offsets.");
                }

                events.Add(ev);
                eventCount++;
                position = checked(position + 1);
            }

            ct.ThrowIfCancellationRequested();
            aggregate.LoadDuringOperation(events, position);
            outcome = "success";
            return aggregate;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            PortiaTelemetry.AggregateFinished(started, "hydrate", outcome, eventCount);
        }
    }

    /// <inheritdoc />
    public ValueTask SaveAsync<TAggregate>(TAggregate aggregate, IExecutionContext context,
        CancellationToken ct = default)
        where TAggregate : Aggregate => SaveCoreAsync(aggregate, EventAttribution.FromContext(context), ct);

    async ValueTask SaveCoreAsync<TAggregate>(TAggregate aggregate, EventAttribution attribution, CancellationToken ct)
        where TAggregate : Aggregate
    {
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "fault";
        var eventCount = 0;
        try
        {
            ArgumentNullException.ThrowIfNull(aggregate);
            using var operation = aggregate.BeginOperation();
            ct.ThrowIfCancellationRequested();
            if (aggregate.UncommittedEvents.Count != 0 && aggregate.UncommittedAudits.Count != 0)
            {
                throw new InvalidOperationException("An aggregate save cannot contain both raised events and audits.");
            }

            if (aggregate.UncommittedEvents.Count == 0 && aggregate.UncommittedAudits.Count == 0)
            {
                outcome = "success";
                return;
            }

            var isAudit = aggregate.UncommittedAudits.Count != 0;
            DomainEvent[] pending = isAudit ? [.. aggregate.UncommittedAudits] : [.. aggregate.UncommittedEvents];
            eventCount = pending.Length;
            DomainEventValidation.ValidateBatch(pending);
            aggregate.PrepareSave(attribution);
            var stream = isAudit ? aggregate.GetAuditSessionStream() : aggregate.Stream;
            var expectedPosition = isAudit ? 0 : aggregate.CommittedStreamPosition;
            await store.AppendAsync(stream, expectedPosition, pending, ct).ConfigureAwait(false);
            aggregate.Save();
            outcome = "success";
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            PortiaTelemetry.AggregateFinished(started, "save", outcome, eventCount);
        }
    }
}

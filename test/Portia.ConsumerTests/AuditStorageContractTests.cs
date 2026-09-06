using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cntryl.Portia.Consumer;

public sealed class AuditStorageContractTests
{
    readonly RequestDispatchContext _saveContext = new(RequestActor.System);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditOnlyPagesPreserveOffsetsAndDoNotEnterAggregateHistory(bool fitz)
    {
        await using var fixture = await StoreFixture.CreateAsync(fitz);
        var account = new Account(Uuid.CreateVersion4());
        for (var index = 0; index < 1025; index++)
            account.Audit(new Declined(index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await fixture.Repository.SaveAsync(account, _saveContext);
        var scenario = new AggregateScenario<Account>(account);
        Assert.Empty(scenario.PendingAudits);
        Assert.Empty(scenario.CommittedEvents);
        Assert.Equal(0UL, account.CommittedStreamPosition);
        EventStreamAddress? session = null;
        await foreach (var record in fixture.Store.ReadAsync(EventStreamPattern.ForPattern(account.Stream.Realm, account.Stream.Area)))
        {
            if (record.Ev.Metadata.AggregateId == account.Id)
            {
                session = record.Stream;
                break;
            }
        }
        Assert.NotNull(session);
        var records = new List<DomainEventRecord>();
        await foreach (var record in fixture.Store.ReadAsync(session))
            records.Add(record);
        Assert.Equal(1025, records.Count);
        Assert.Equal(1024UL, records[^1].ResourceOffset);
        Assert.All(records, record =>
        {
            Assert.True(record.Ev.Metadata.IsAudit);
            Assert.Equal(0UL, record.Ev.Metadata.AggregateVersion);
        });
        Assert.Equal(0UL, (await fixture.Repository.HydrateAsync(new Account(account.Id))).CommittedStreamPosition);
    }

    [Fact]
    public void StoredEventsWithoutAuditFlagRemainNonAudits()
    {
        var serializer = new JsonDomainEventSerializer(new DomainEventTypeCatalog().Register<Deposited>());
        var ev = DomainEventSeed.Attach(new Deposited(3), Uuid.CreateVersion4(), 1);
        var envelope = JsonNode.Parse(serializer.Serialize(ev).Span)!.AsObject();
        Assert.True(envelope["metadata"]!.AsObject().Remove("is_audit"));
        var stored = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var loaded = serializer.Deserialize(stored);
        Assert.False(loaded.Metadata.IsAudit);
        Assert.Equal(ev.Metadata, loaded.Metadata);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateEmissionOwnsAuditClassification(bool audit)
    {
        var account = new Account(Uuid.CreateVersion4(), new MisclassifyingFactory(audit));
        var ev = new Deposited(1);
        if (audit)
            account.Audit(ev);
        else
            account.Raise(ev);
        Assert.Equal(audit, ev.Metadata.IsAudit);
        var serializer = new JsonDomainEventSerializer(new DomainEventTypeCatalog().Register<Deposited>());
        Assert.Equal(ev.Metadata, serializer.Deserialize(serializer.Serialize(ev)).Metadata);
    }

    sealed class MisclassifyingFactory(bool audit) : IDomainEventMetadataFactory
    {
        public DomainEventMetadata Create(Uuid aggregateId, ulong aggregateVersion)
            => new(Uuid.CreateVersion4(), aggregateId, aggregateVersion, DateTimeOffset.UtcNow, IsAudit: !audit);
    }
}

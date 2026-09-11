using System.Globalization;

namespace Cntryl.Portia.Consumer;

public sealed class AggregatePersistenceTests
{
    readonly RequestDispatchContext _saveContext = new(RequestActor.System);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditBatchesUseDistinctVersionFourSessionStreamsWithoutChangingAggregateVersion(bool fitz)
    {
        await using var fixture = await StoreFixture.CreateAsync(fitz);
        var repository = fixture.Repository;
        var account = new Account(Uuid.CreateVersion4());
        account.Deposit(10);
        await repository.SaveAsync(account, _saveContext);
        var competingWriter = Assert.IsType<Account>(await repository.HydrateAsync(new Account(account.Id)));
        account.Audit(new Declined("limit"));
        account.Audit(new Declined("reviewed"));
        await repository.SaveAsync(account, _saveContext);
        Assert.Equal(1UL, account.CommittedStreamPosition);
        account.Audit(new Declined("next session"));
        await repository.SaveAsync(account, _saveContext);

        // Audit writes do not conflict with a writer loaded from the same raised-event version.
        competingWriter.Deposit(5);
        await repository.SaveAsync(competingWriter, _saveContext);
        var reloaded = Assert.IsType<Account>(await repository.HydrateAsync(new Account(account.Id)));
        Assert.Equal(15, reloaded.Balance);
        Assert.Equal(2UL, reloaded.Version);
        Assert.Equal(2UL, reloaded.CommittedStreamPosition);
        var events = new List<DomainEventRecord>();
        await foreach (var record in fixture.Store.ReadAsync(account.Stream))
            events.Add(record);
        Assert.Equal([0UL, 1UL], events.Select(record => record.ResourceOffset));
        Assert.All(events, record => Assert.False(record.Event.Metadata.IsAudit));

        var records = new List<DomainEventRecord>();
        await foreach (var record in fixture.Store.ReadAsync(
                           EventStreamPattern.ForPattern(account.Stream.Realm, account.Stream.Area)))
        {
            if (record.Event.Metadata.AggregateId == account.Id)
            {
                records.Add(record);
            }
        }

        Assert.Equal(5, records.Count);
        var sessions = records.Where(record => record.Event.Metadata.IsAudit).GroupBy(record => record.Stream)
            .ToArray();
        Assert.Equal(2, sessions.Length);
        Assert.Equal([1, 2], sessions.Select(session => session.Count()).Order());
        Assert.All(sessions, session =>
        {
            Assert.NotEqual(account.Stream, session.Key);
            Assert.Equal(4, Uuid.Parse(session.Key.Resource, CultureInfo.InvariantCulture).Version);
            Assert.Equal(0UL, session.First().ResourceOffset);
            Assert.All(session, record => Assert.Equal(1UL, record.Event.Metadata.AggregateVersion));
        });
        var multiAuditSession = sessions.Single(session => session.Count() == 2);
        var tail = new List<DomainEventRecord>();
        await foreach (var record in fixture.Store.ReadAsync(multiAuditSession.Key, 1))
            tail.Add(record);
        Assert.Equal(1UL, Assert.Single(tail).ResourceOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditBeforeCreationLeavesAggregateStreamAbsent(bool fitz)
    {
        await using var fixture = await StoreFixture.CreateAsync(fitz);
        var account = new Account(Uuid.CreateVersion4());
        account.Audit(new Declined("declined before creation"));
        await fixture.Repository.SaveAsync(account, _saveContext);
        Assert.Equal(0UL, (await fixture.Repository.HydrateAsync(new Account(account.Id))).CommittedStreamPosition);
        Assert.Equal(0, account.Balance);
        Assert.Equal(0UL, account.Version);
        Assert.Equal(0UL, account.CommittedStreamPosition);
        account.Deposit(3);
        await fixture.Repository.SaveAsync(account, _saveContext);
        Assert.Equal(3, (await fixture.Repository.HydrateAsync(new Account(account.Id)))!.Balance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingRaisedEventsStillConflictAndKeepPendingChanges(bool fitz)
    {
        await using var fixture = await StoreFixture.CreateAsync(fitz);
        var account = new Account(Uuid.CreateVersion4());
        account.Deposit(1);
        await fixture.Repository.SaveAsync(account, _saveContext);
        var stale = Assert.IsType<Account>(await fixture.Repository.HydrateAsync(new Account(account.Id)));
        account.Deposit(2);
        await fixture.Repository.SaveAsync(account, _saveContext);
        stale.Deposit(4);
        _ = await Assert.ThrowsAsync<EventStreamConcurrencyException>(() =>
            fixture.Repository.SaveAsync(stale, _saveContext).AsTask());
        Assert.Equal(1UL, stale.CommittedStreamPosition);
        Assert.Equal(2UL, stale.Version);
        Assert.Equal(4,
            Assert.IsType<Deposited>(Assert.Single(new AggregateScenario<Account>(stale).PendingEvents)).Amount);
        Assert.Equal(3, (await fixture.Repository.HydrateAsync(new Account(account.Id)))!.Balance);
    }

    [Fact]
    public async Task PublicRegistrationSavesAndReloadsAggregate()
    {
        var assembly = GeneratorCompilation.Compile("""
                                                    using System;
                                                    using System.Threading.Tasks;
                                                    using Cntryl.Portia;
                                                    using Cntryl.Portia.Testing;
                                                    using Cntryl.Portia.Consumer;
                                                    using Microsoft.Extensions.DependencyInjection;
                                                    public static class Scenario
                                                    {
                                                        public static async Task<int> Run()
                                                        {
                                                            var services = new ServiceCollection();
                                                            services.AddSingleton<IEventStore, InMemoryEventStore>();
                                                            services.AddPortia();
                                                            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
                                                            {
                                                                ValidateScopes = true, ValidateOnBuild = true,
                                                            });
                                                            await using var scope = provider.CreateAsyncScope();
                                                            var repository = scope.ServiceProvider.GetRequiredService<IAggregateRepository>();
                                                            var account = new Account(Uuid.CreateVersion4());
                                                            if ((await repository.HydrateAsync(new Account(account.Id))).CommittedStreamPosition != 0)
                                                                throw new Exception("An absent stream must leave the constructed instance unchanged.");
                                                            account.Deposit(12);
                                                            await repository.SaveAsync(account, new RequestDispatchContext(RequestActor.System));
                                                            var loaded = await repository.HydrateAsync(new Account(account.Id));
                                                            return loaded?.Balance ?? -1;
                                                        }
                                                    }
                                                    """);
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<int>>>();
        Assert.Equal(12, await run());
    }
}

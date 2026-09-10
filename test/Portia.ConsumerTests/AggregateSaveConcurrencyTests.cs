using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class AggregateSaveConcurrencyTests
{
    readonly RequestDispatchContext _saveContext = new(RequestActor.System);
    [Theory]
    [InlineData("event")]
    [InlineData("audit")]
    [InlineData("save")]
    public async Task RejectsConcurrentEmissionOrSave(string operation)
    {
        var store = new ControlledStore { BlockFirstAppend = true };
        await using var provider = CreateProvider(store);
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAggregateRepository>();
        var aggregate = new Account(Uuid.CreateVersion4());
        if (operation == "audit")
            aggregate.Audit(new Declined("first"));
        else
            aggregate.Deposit(1);
        var saving = repository.SaveAsync(aggregate, _saveContext).AsTask();
        await store.Entered.Task;
        try
        {
            _ = operation == "save"
                ? await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(aggregate, _saveContext).AsTask())
                : operation == "audit"
                ? Assert.Throws<InvalidOperationException>(() => aggregate.Audit(new Declined("second")))
                : Assert.Throws<InvalidOperationException>(() => aggregate.Deposit(2));
        }
        finally
        {
            store.Release.SetResult();
            await saving;
        }
        Assert.Equal(1, store.AppendCalls);
        Assert.Equal(operation == "audit" ? 0UL : 1UL, aggregate.CommittedStreamPosition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSavePreservesPayloadIdentityVersionAndKind(bool audit)
    {
        var store = new ControlledStore { FailAppend = true };
        await using var provider = CreateProvider(store);
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAggregateRepository>();
        var aggregate = new Account(Uuid.CreateVersion4());
        var scenario = new AggregateScenario<Account>(aggregate);
        var payload = new Deposited(7);
        if (audit)
            aggregate.Audit(payload);
        else
            aggregate.Raise(payload);
        var metadata = payload.Metadata;
        _ = await Assert.ThrowsAsync<IOException>(() => repository.SaveAsync(aggregate, _saveContext).AsTask());
        Assert.Same(payload, Assert.Single(audit ? scenario.PendingAudits : scenario.PendingEvents));
        Assert.Equal(metadata, payload.Metadata with
        {
            CorrelationId = null,
            CausationId = null,
            ExecutionId = null,
            Actor = null,
        });
        Assert.Empty(scenario.CommittedEvents);
        Assert.Equal(0UL, aggregate.CommittedStreamPosition);
        Assert.Equal(audit ? 0UL : 1UL, aggregate.Version);
        _ = audit
            ? Assert.Throws<InvalidOperationException>(() => aggregate.Deposit(1))
            : Assert.Throws<InvalidOperationException>(() => aggregate.Audit(new Declined("rejected")));

        store.FailAppend = false;
        await repository.SaveAsync(aggregate, _saveContext);
        Assert.Empty(scenario.PendingAudits);
        Assert.Empty(scenario.PendingEvents);
        Assert.Equal(audit ? 0 : 1, scenario.CommittedEvents.Count);
        Assert.Equal(audit ? 0UL : 1UL, aggregate.CommittedStreamPosition);
        Assert.Equal(store.Routes[0], store.Routes[1]);
        if (audit)
        {
            Assert.Equal(4, Uuid.Parse(store.Routes[0].Resource, CultureInfo.InvariantCulture).Version);
            Assert.NotEqual(aggregate.Stream, store.Routes[0]);
            aggregate.Audit(new Declined("new session"));
            await repository.SaveAsync(aggregate, _saveContext);
            Assert.NotEqual(store.Routes[0], store.Routes[2]);
        }
    }

    [Fact]
    public async Task EmptySaveDoesNotAppend()
    {
        var store = new ControlledStore { FailAppend = true };
        await using var provider = CreateProvider(store);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAggregateRepository>().SaveAsync(new Account(Uuid.CreateVersion4()), _saveContext);
        Assert.Equal(0, store.AppendCalls);
    }

    static ServiceProvider CreateProvider(IEventStore store)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton(store);
        _ = services.AddPortia();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    sealed class ControlledStore : IEventStore
    {
        readonly InMemoryEventStore _inner = new();

        public bool FailAppend { get; set; }

        public bool BlockFirstAppend { get; init; }

        public int AppendCalls { get; private set; }

        public List<EventStreamAddress> Routes { get; } = [];

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0, CancellationToken ct = default)
            => _inner.ReadAsync(stream, fromOffset, ct);

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong fromOffset = 0, CancellationToken ct = default)
            => _inner.ReadAsync(pattern, fromOffset, ct);

        public async ValueTask AppendAsync(EventStreamAddress stream, ulong expectedStreamPosition, IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            AppendCalls++;
            Routes.Add(stream);
            if (BlockFirstAppend && AppendCalls == 1)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(ct);
            }
            else if (BlockFirstAppend)
            {
                // Do not serialize two saves in the test double: the aggregate must reject the second one.
                return;
            }
            if (FailAppend)
                throw new IOException("Injected append failure");
            await _inner.AppendAsync(stream, expectedStreamPosition, events, ct);
        }
    }
}

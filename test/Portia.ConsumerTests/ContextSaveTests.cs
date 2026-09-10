using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class ContextSaveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSaveFreezesAttributionAndRetryPreservesSerializedEvents(bool audit)
    {
        var store = new FailingStore();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var repository = new AggregateRepository(store);
        var id = Uuid.CreateVersion4();
        var actor = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice", ClaimValueTypes.String, "accounts")],
                "test"));
        var context = new RequestContext<DepositAccount>(new DepositAccount(id, 10), actor);
        var account = new Account(id);
        if (audit)
        {
            account.Audit(new Declined("declined"));
        }
        else
        {
            account.Deposit(10);
        }

        _ = await Assert.ThrowsAsync<IOException>(async () => await repository.SaveAsync(account, context));
        _ = Assert.Throws<InvalidOperationException>(() =>
        {
            if (audit)
            {
                account.Audit(new Declined("cannot change an unconfirmed batch"));
            }
            else
            {
                account.Deposit(99);
            }
        });
        var first = store.Writes.Single();
        var metadata = store.Serializer.Deserialize(first).Metadata;
        Assert.Equal(context.RequestId, metadata.CausationId);
        Assert.Equal(context.CorrelationId, metadata.CorrelationId);
        Assert.Equal(context.ExecutionId, metadata.ExecutionId);
        Assert.Equal(new ActorAttribution("alice", "accounts"), metadata.Actor);
        var replacement = new RequestContext<DepositAccount>(context.Request, RequestActor.System);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.SaveAsync(account, replacement));
        _ = Assert.Single(store.Writes);
        // Mutation of the input principal or an exposed snapshot cannot rewrite the frozen attribution.
        ((ClaimsIdentity)actor.Identity!).AddClaim(new Claim("role", "admin"));
        ((ClaimsIdentity)context.Actor.Identity!).AddClaim(new Claim("role", "owner"));
        await repository.SaveAsync(account, context);
        Assert.Equal(first, store.Writes[1]);
        Assert.Equal(audit ? 0UL : 1UL, account.CommittedStreamPosition);
        if (audit)
        {
            account.Audit(new Declined("another"));
        }
        else
        {
            account.Deposit(5);
        }

        await repository.SaveAsync(account, replacement);
        Assert.Equal(replacement.ExecutionId, store.Serializer.Deserialize(store.Writes[2]).Metadata.ExecutionId);
    }

    [Fact]
    public async Task MissingContextCannotPersistEvents()
    {
        var store = new InMemoryEventStore();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        account.Deposit(1);
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () => await repository.SaveAsync(account, null!));
        Assert.Equal(0UL, account.CommittedStreamPosition);
    }

    [Fact]
    public async Task CancelledSaveDoesNotFreezeAttribution()
    {
        var store = new InMemoryEventStore();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        account.Deposit(3);
        var first = new RequestContext<DepositAccount>(new DepositAccount(account.Id, 3), RequestActor.System);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.SaveAsync(account, first, cancellation.Token));
        var second = new RequestContext<DepositAccount>(first.Request, RequestActor.System);
        await repository.SaveAsync(account, second);
        await foreach (var record in store.ReadAsync(account.Stream))
            Assert.Equal(second.ExecutionId, record.Event.Metadata.ExecutionId);
    }

    [Fact]
    public async Task ConflictingMetadataRejectsWholeBatchBeforeStampingOrAppending()
    {
        var store = new InMemoryEventStore();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4(), new ConflictingFactory());
        var first = new Deposited(1);
        var second = new Deposited(2);
        account.Raise(first);
        account.Raise(second);
        var original = first.Metadata;
        var context = new RequestDispatchContext(RequestActor.System);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.SaveAsync(account, context));
        Assert.Same(original, first.Metadata);
        Assert.Equal(0UL, account.CommittedStreamPosition);
        await foreach (var record in store.ReadAsync(account.Stream))
            Assert.Fail($"Unexpected appended event {record.Event.Metadata.EventId}");
    }

    sealed class ConflictingFactory : IDomainEventMetadataFactory
    {
        public DomainEventMetadata Create(Uuid aggregateId, ulong aggregateVersion)
            => new(Uuid.CreateVersion4(), aggregateId, aggregateVersion, DateTimeOffset.UtcNow,
                aggregateVersion == 2 ? Uuid.CreateVersion4() : null);
    }

    sealed class FailingStore : IEventStore
    {
        readonly InMemoryEventStore _inner = new();

        public JsonDomainEventSerializer Serializer { get; } = ConsumerJson.DomainSerializer(
            new DomainEventTypeCatalog().Register<Deposited>(1, "Deposited").Register<Declined>(1, "Declined"));

        public List<byte[]> Writes { get; } = [];

        public ValueTask AppendAsync(EventStreamAddress stream, ulong expectedStreamPosition,
            IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            Writes.Add(Serializer.Serialize(Assert.Single(events)).ToArray());
            return Writes.Count == 1
                ? throw new IOException("Unconfirmed append")
                : _inner.AppendAsync(stream, expectedStreamPosition, events, ct);
        }

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0,
            CancellationToken ct = default)
            => _inner.ReadAsync(stream, fromOffset, ct);

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong fromOffset = 0,
            CancellationToken ct = default)
            => _inner.ReadAsync(pattern, fromOffset, ct);
    }
}

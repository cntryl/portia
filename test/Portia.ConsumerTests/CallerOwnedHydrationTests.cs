using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class CallerOwnedHydrationTests
{
    readonly RequestDispatchContext _saveContext = new(RequestActor.System);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerConstructsHydratesChangesAndSavesWithoutAnyFactory(bool fitz)
    {
        await using var fixture = await StoreFixture.CreateAsync(fitz);
        var services = new ServiceCollection();
        _ = services.AddSingleton(fixture.Store);
        _ = services.AddPortia();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAggregateRepository>();
        var id = Uuid.CreateVersion4();
        var original = new Account(id);
        Assert.Same(original, await repository.HydrateAsync(original));
        Assert.Equal(0UL, original.Version);
        original.Deposit(12);
        await repository.SaveAsync(original, _saveContext);
        original.Audit(new Declined("test"));
        await repository.SaveAsync(original, _saveContext);
        var loaded = new Account(id);
        Assert.Same(loaded, await repository.HydrateAsync(loaded));
        Assert.Equal(12, loaded.Balance);
        Assert.Equal(1UL, loaded.Version);
        loaded.Deposit(8);
        await repository.SaveAsync(loaded, _saveContext);
        Assert.Equal(20, (await repository.HydrateAsync(new Account(id))).Balance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HydrationRejectsPendingChangesWithoutDiscardingThem(bool audit)
    {
        var store = new InMemoryEventStore();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        if (audit)
            account.Audit(new Declined("pending"));
        else
            account.Deposit(5);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.HydrateAsync(account));
        await repository.SaveAsync(account, _saveContext);
        Assert.Equal(audit ? 0UL : 1UL, account.CommittedStreamPosition);
        Assert.Same(account, await repository.HydrateAsync(account));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HydrationResumesAfterCommittedEventsAndCanSaveAgain(bool fitz)
    {
        await using var fixture = await StoreFixture.CreateAsync(fitz);
        var repository = fixture.Repository;
        var id = Uuid.CreateVersion4();
        var first = new Account(id);
        first.Deposit(10);
        await repository.SaveAsync(first, _saveContext);
        var second = await repository.HydrateAsync(new Account(id));
        second.Deposit(20);
        await repository.SaveAsync(second, _saveContext);
        Assert.Same(first, await repository.HydrateAsync(first));
        Assert.Equal(30, first.Balance);
        Assert.Equal(2UL, first.Version);
        Assert.Equal(2UL, first.CommittedStreamPosition);
        Assert.Same(first, await repository.HydrateAsync(first));
        Assert.Equal(30, first.Balance);
        first.Deposit(7);
        await repository.SaveAsync(first, _saveContext);
        Assert.Equal(37, (await repository.HydrateAsync(second)).Balance);
    }

    [Fact]
    public async Task ReadsOnlyNewOffsetsAndExcludesConcurrentMutationForEntireRead()
    {
        var store = new TrackingStore();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        account.Deposit(3);
        await repository.SaveAsync(account, _saveContext);
        var peer = await repository.HydrateAsync(new Account(account.Id));
        peer.Deposit(4);
        await repository.SaveAsync(peer, _saveContext);
        store.Pause = true;
        var hydration = repository.HydrateAsync(account).AsTask();
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            _ = Assert.Throws<InvalidOperationException>(() => account.Deposit(100));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.SaveAsync(account, _saveContext));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.HydrateAsync(account));
            Assert.Equal(3, account.Balance);
        }
        finally { _ = store.Continue.TrySetResult(); }
        Assert.Same(account, await hydration);
        Assert.Equal(7, account.Balance);
        Assert.Equal(new ulong[] { 0, 1 }, store.Offsets);
    }

    sealed class TrackingStore : IEventStore
    {
        readonly InMemoryEventStore _inner = new();
        public List<ulong> Offsets { get; } = [];
        public bool Pause { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Offsets.Add(fromOffset);
            if (Pause)
            {
                _ = Entered.TrySetResult();
                await Continue.Task.WaitAsync(ct);
            }
            await foreach (var record in _inner.ReadAsync(stream, fromOffset, ct))
                yield return record;
        }

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong fromOffset = 0, CancellationToken ct = default)
            => _inner.ReadAsync(pattern, fromOffset, ct);

        public ValueTask AppendAsync(EventStreamAddress stream, ulong expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
            => _inner.AppendAsync(stream, expectedVersion, events, ct);
    }

    [Fact]
    public async Task CancellationLeavesFreshInstanceAvailableForRetry()
    {
        var store = new InMemoryEventStore();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repository.HydrateAsync(account, cancellation.Token));
        Assert.Same(account, await repository.HydrateAsync(account));
    }
}

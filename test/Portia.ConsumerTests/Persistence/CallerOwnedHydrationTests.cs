using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

[Trait("Category", "BrokerIntegration")]
public sealed class CallerOwnedHydrationTests
{
    readonly RequestDispatchContext _saveContext = new(RequestActor.System);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldConstructHydrateChangeAndSaveGivenCallerOwnedAggregateWithoutFactory(bool fitz)
    {
        // Arrange
        await using var fixture = await StoreFixture.CreateAsync(fitz);
        var services = new ServiceCollection();
        _ = services.AddSingleton(fixture.Store);
        _ = services.AddPortia();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.Aggregates();
        var id = Uuid.CreateVersion4();

        // Act
        var original = new Account(id);

        // Assert
        Assert.Same(original, await repository.HydrateAsync(original, TestContext.Current.CancellationToken));
        Assert.Equal(0UL, original.Version);
        original.Deposit(12);
        await repository.SaveAsync(original, _saveContext, TestContext.Current.CancellationToken);
        original.Audit(new Declined("test"));
        await repository.SaveAsync(original, _saveContext, TestContext.Current.CancellationToken);
        var loaded = new Account(id);
        Assert.Same(loaded, await repository.HydrateAsync(loaded, TestContext.Current.CancellationToken));
        Assert.Equal(12, loaded.Balance);
        Assert.Equal(1UL, loaded.Version);
        loaded.Deposit(8);
        await repository.SaveAsync(loaded, _saveContext, TestContext.Current.CancellationToken);
        Assert.Equal(20, (await repository.HydrateAsync(new Account(id), TestContext.Current.CancellationToken)).Balance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectHydrationAndKeepPendingChangesGivenAggregateWithPendingChanges(bool audit)
    {
        // Arrange
        var store = new InMemoryEventStore();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());

        // Act
        if (audit)
        {
            account.Audit(new Declined("pending"));
        }
        else
        {
            account.Deposit(5);
        }

        // Assert
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.HydrateAsync(account, TestContext.Current.CancellationToken));
        await repository.SaveAsync(account, _saveContext, TestContext.Current.CancellationToken);
        Assert.Equal(audit ? 0UL : 1UL, account.CommittedStreamPosition);
        Assert.Same(account, await repository.HydrateAsync(account, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldResumeAndSaveAgainGivenHydratedAggregateWithCommittedEvents(bool fitz)
    {
        // Arrange
        await using var fixture = await StoreFixture.CreateAsync(fitz);
        var repository = fixture.Repository;
        var id = Uuid.CreateVersion4();
        var first = new Account(id);
        first.Deposit(10);
        await repository.SaveAsync(first, _saveContext, TestContext.Current.CancellationToken);
        var second = await repository.HydrateAsync(new Account(id), TestContext.Current.CancellationToken);
        second.Deposit(20);

        // Act
        await repository.SaveAsync(second, _saveContext, TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(first, await repository.HydrateAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal(30, first.Balance);
        Assert.Equal(2UL, first.Version);
        Assert.Equal(2UL, first.CommittedStreamPosition);
        Assert.Same(first, await repository.HydrateAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal(30, first.Balance);
        first.Deposit(7);
        await repository.SaveAsync(first, _saveContext, TestContext.Current.CancellationToken);
        Assert.Equal(37, (await repository.HydrateAsync(second, TestContext.Current.CancellationToken)).Balance);
    }

    [Fact]
    public async Task ShouldReadOnlyNewOffsetsGivenConcurrentMutationWhenHydratingPeer()
    {
        // Arrange
        var store = new TrackingStore();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        account.Deposit(3);
        await repository.SaveAsync(account, _saveContext, TestContext.Current.CancellationToken);
        var peer = await repository.HydrateAsync(new Account(account.Id), TestContext.Current.CancellationToken);
        peer.Deposit(4);
        await repository.SaveAsync(peer, _saveContext, TestContext.Current.CancellationToken);
        store.Pause = true;
        var hydration = repository.HydrateAsync(account, TestContext.Current.CancellationToken).AsTask();

        // Act
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        try
        {
            _ = Assert.Throws<InvalidOperationException>(() => account.Deposit(100));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await repository.SaveAsync(account, _saveContext, TestContext.Current.CancellationToken));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.HydrateAsync(account, TestContext.Current.CancellationToken));
            Assert.Equal(3, account.Balance);
        }
        finally
        {
            _ = store.Continue.TrySetResult();
        }

        Assert.Same(account, await hydration);
        Assert.Equal(7, account.Balance);
        Assert.Equal(new ulong[] { 0, 1 }, store.Offsets);
    }

    [Fact]
    public async Task ShouldLeaveFreshInstanceAvailableForRetryGivenCancelledHydration()
    {
        // Arrange
        var store = new InMemoryEventStore();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        using var cancellation = new CancellationTokenSource();

        // Act
        await cancellation.CancelAsync();

        // Assert
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.HydrateAsync(account, cancellation.Token));
        Assert.Same(account, await repository.HydrateAsync(account, TestContext.Current.CancellationToken));
    }

    sealed class TrackingStore : IEventStore
    {
        readonly InMemoryEventStore _inner = new();
        public List<ulong> Offsets { get; } = [];
        public bool Pause { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0,
            [EnumeratorCancellation] CancellationToken ct = default)
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

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor,
            CancellationToken ct = default)
            => _inner.ReadAsync(pattern, cursor, ct);

        public ValueTask AppendAsync(EventStreamAddress stream, ulong expectedVersion,
            IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
            => _inner.AppendAsync(stream, expectedVersion, events, ct);
    }
}

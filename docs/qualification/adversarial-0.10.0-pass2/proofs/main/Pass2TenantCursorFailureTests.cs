namespace Cntryl.Portia;

/// <summary>Exercises cursor resumption after processing and cleanup failures.</summary>
public sealed class Pass2TenantCursorFailureTests
{
    static readonly EventStreamAddress Registry = new("global", "tenants", "registry");
    static readonly EventStreamPattern Pattern = EventStreamPattern.ForPattern("global", "tenants", "registry");

    /// <summary>A record whose tenant extraction failed must remain pending on the same cursor.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRetryLifecycleRecordAfterExtractorFailure(bool removal)
    {
        var events = new InMemoryEventStore();
        var aggregate = Uuid.CreateVersion4();
        await AppendAsync(events, aggregate, 0, new Started("acme"));
        var failOnce = !removal;
        var directory = new EventSourcedTenantDirectory<Started, Stopped>(events, Pattern, ev =>
        {
            if (failOnce)
            {
                failOnce = false;
                throw new IOException("tenant extraction failed once");
            }
            return GetTenant(ev);
        });
        await using var cursor = await directory.OpenCursorAsync();
        if (removal)
        {
            Assert.Equal(Added("acme"), await ReadOneAsync(cursor));
            await AppendAsync(events, aggregate, 1, new Stopped("acme"));
            failOnce = true;
        }
        await AppendAsync(events, aggregate, removal ? 2UL : 1UL, new Started("beta"));

        _ = await Assert.ThrowsAsync<IOException>(() => ReadOneAsync(cursor));

        var expected = removal
            ? new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, new TenantId("acme"))
            : Added("acme");
        Assert.Equal(expected, await ReadOneAsync(cursor));
    }

    /// <summary>A subscription cleanup fault must release the cursor's enumeration reservation.</summary>
    [Fact]
    public async Task ShouldResumeCursorAfterSubscriptionDisposalFails()
    {
        var events = new InMemoryEventStore();
        var aggregate = Uuid.CreateVersion4();
        await AppendAsync(events, aggregate, 0, new Started("acme"));
        var notifier = new CleanupNotifier(true);
        var directory = new EventSourcedTenantDirectory<Started, Stopped>(events, Pattern, GetTenant,
            notifier: notifier);
        await using var cursor = await directory.OpenCursorAsync();
        var first = cursor.ReadAsync().GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        Assert.Equal(Added("acme"), first.Current);
        _ = await Assert.ThrowsAsync<IOException>(() => first.DisposeAsync().AsTask());
        await AppendAsync(events, aggregate, 1, new Started("beta"));

        Assert.Equal(Added("beta"), await ReadOneAsync(cursor));
    }

    /// <summary>The same cursor resumes normally after successful subscription disposal.</summary>
    [Fact]
    public async Task ShouldResumeCursorAfterOrdinarySubscriptionDisposal()
    {
        var events = new InMemoryEventStore();
        var aggregate = Uuid.CreateVersion4();
        await AppendAsync(events, aggregate, 0, new Started("acme"));
        var directory = new EventSourcedTenantDirectory<Started, Stopped>(events, Pattern, GetTenant,
            notifier: new CleanupNotifier(false));
        await using var cursor = await directory.OpenCursorAsync();
        Assert.Equal(Added("acme"), await ReadOneAsync(cursor));
        await AppendAsync(events, aggregate, 1, new Started("beta"));

        Assert.Equal(Added("beta"), await ReadOneAsync(cursor));
    }

    static TenantLifecycleChange Added(string tenant) => new(TenantLifecycleChangeKind.Added, new TenantId(tenant));

    static TenantId GetTenant(DomainEvent ev) => new(ev switch
    {
        Started started => started.Tenant,
        Stopped stopped => stopped.Tenant,
        _ => throw new InvalidOperationException("Unexpected lifecycle event.")
    });

    static async Task AppendAsync(InMemoryEventStore events, Uuid aggregate, ulong offset, DomainEvent ev)
    {
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregate, offset + 1, DateTimeOffset.UtcNow));
        await events.AppendAsync(Registry, offset, [ev]);
    }

    static async Task<TenantLifecycleChange> ReadOneAsync(ITenantDirectoryCursor cursor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = cursor.ReadAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        return reader.Current;
    }

    sealed record Started(string Tenant) : DomainEvent;
    sealed record Stopped(string Tenant) : DomainEvent;

    sealed class CleanupNotifier(bool failFirstDispose) : IDomainEventNotifier
    {
        int _subscriptions;

        public ValueTask<IDomainEventSubscription> SubscribeAsync(EventStreamPattern pattern, CancellationToken ct = default)
            => ValueTask.FromResult<IDomainEventSubscription>(new Subscription(failFirstDispose && _subscriptions++ == 0));

        sealed class Subscription(bool failDispose) : IDomainEventSubscription
        {
            public ValueTask WaitAsync(CancellationToken ct = default) => new(Task.Delay(Timeout.Infinite, ct));

            public ValueTask DisposeAsync() => failDispose
                ? ValueTask.FromException(new IOException("subscription disposal failed"))
                : ValueTask.CompletedTask;
        }
    }
}
